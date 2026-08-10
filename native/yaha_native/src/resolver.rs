//! Name resolution: an optional external (managed) resolver, plus a stale-result fallback cache.
//!
//! # Why this exists
//!
//! The default resolver is hyper-util's `GaiResolver`, which is `getaddrinfo` on a blocking thread.
//! On Android that is the wrong abstraction in two ways:
//!
//!  * `getaddrinfo` resolves on whatever network the *process* is bound to. If the app is
//!    backgrounded and that network goes away, every subsequent lookup fails with `EAI_NODATA`
//!    ("No address associated with hostname") until the process re-binds - Android's own docs for
//!    `bindProcessToNetwork` spell this out. The platform-correct fix is to resolve against the
//!    currently active `Network` object, which is only reachable from managed/Java code.
//!  * Even when the platform resolver is healthy, a transient failure right after resume turns
//!    into a hard request failure, when the previously-resolved addresses would almost certainly
//!    still have worked.
//!
//! [`YahaResolver`] addresses both: it can delegate lookups to a managed callback, and it keeps the
//! last successful answer per host so it has something to fall back on when a lookup fails.

use std::collections::HashMap;
use std::future::Future;
use std::io;
use std::net::{Ipv4Addr, Ipv6Addr, SocketAddr, SocketAddrV4, SocketAddrV6};
use std::num::NonZeroIsize;
use std::pin::Pin;
use std::sync::{Arc, Mutex};
use std::task::{self, Poll};
use std::time::{Duration, Instant};

use hyper_util::client::legacy::connect::dns::{GaiResolver, Name};
use tower_service::Service;

/// `family` value marking [`YahaSocketAddress::address`] as a 4-byte IPv4 address.
pub const YAHA_ADDRESS_FAMILY_IPV4: i32 = 4;
/// `family` value marking [`YahaSocketAddress::address`] as a 16-byte IPv6 address.
pub const YAHA_ADDRESS_FAMILY_IPV6: i32 = 6;

/// Maximum number of addresses a single lookup may return. Anything beyond this is dropped;
/// connection attempts walk the list in order, so a truncated list is a slower failover at worst.
pub const YAHA_MAX_RESOLVED_ADDRESSES: usize = 32;

/// Number of hosts the fallback cache retains. Well beyond what a game client talks to.
const MAX_CACHE_ENTRIES: usize = 256;

/// One resolved address. The managed resolver writes these into a buffer owned by the caller, so
/// neither side has to allocate or agree on who frees what.
#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct YahaSocketAddress {
    /// [`YAHA_ADDRESS_FAMILY_IPV4`] or [`YAHA_ADDRESS_FAMILY_IPV6`]. Entries with any other value
    /// are skipped, so a managed resolver can leave unused slots zeroed.
    pub family: i32,
    /// IPv6 scope id (interface index). Ignored for IPv4.
    pub scope_id: u32,
    /// Network byte order, matching `IPAddress.GetAddressBytes()`. IPv4 uses the first 4 bytes.
    pub address: [u8; 16],
}

impl YahaSocketAddress {
    /// Converts to a `SocketAddr` with a zero port. hyper fills the real port in afterwards from
    /// the request URI (`set_port` in hyper-util's `HttpConnector`), exactly as it does for
    /// `GaiResolver` results.
    fn to_socket_addr(self) -> Option<SocketAddr> {
        match self.family {
            YAHA_ADDRESS_FAMILY_IPV4 => {
                let mut octets = [0u8; 4];
                octets.copy_from_slice(&self.address[..4]);
                Some(SocketAddr::V4(SocketAddrV4::new(Ipv4Addr::from(octets), 0)))
            }
            YAHA_ADDRESS_FAMILY_IPV6 => Some(SocketAddr::V6(SocketAddrV6::new(
                Ipv6Addr::from(self.address),
                0,
                0,
                self.scope_id,
            ))),
            _ => None,
        }
    }
}

/// Signature of the managed name resolver.
///
/// Writes up to `addresses_capacity` entries into `addresses` and returns the number written.
/// Returning a negative value - or zero - means the lookup failed, and the caller falls back to its
/// cache. Must not throw across the boundary; the managed side catches and reports `-1` instead.
pub type OnResolveDns = extern "C" fn(
    callback_state: NonZeroIsize,
    host: *const u8,
    host_len: usize,
    addresses: *mut YahaSocketAddress,
    addresses_capacity: i32,
) -> i32;

/// Last-known-good addresses per host, used only when a lookup fails.
struct DnsCache {
    max_age: Duration,
    entries: Mutex<HashMap<String, CacheEntry>>,
}

struct CacheEntry {
    addresses: Vec<SocketAddr>,
    stored_at: Instant,
}

impl DnsCache {
    fn new(max_age: Duration) -> Self {
        DnsCache { max_age, entries: Mutex::new(HashMap::new()) }
    }

    fn store(&self, host: &str, addresses: &[SocketAddr]) {
        let Ok(mut entries) = self.entries.lock() else {
            return;
        };

        // Bound the map. Evicting the least recently *stored* entry is good enough: this is a
        // failure-path safety net, not a performance cache.
        if entries.len() >= MAX_CACHE_ENTRIES && !entries.contains_key(host) {
            if let Some(oldest) = entries
                .iter()
                .min_by_key(|(_, entry)| entry.stored_at)
                .map(|(key, _)| key.clone())
            {
                entries.remove(&oldest);
            }
        }

        entries.insert(
            host.to_string(),
            CacheEntry { addresses: addresses.to_vec(), stored_at: Instant::now() },
        );
    }

    fn get(&self, host: &str) -> Option<Vec<SocketAddr>> {
        let mut entries = self.entries.lock().ok()?;
        let entry = entries.get(host)?;

        if entry.stored_at.elapsed() > self.max_age {
            entries.remove(host);
            return None;
        }

        Some(entry.addresses.clone())
    }
}

/// The resolver installed on every `HttpConnector` this library builds.
///
/// Resolution order:
///  1. the managed resolver, if one was registered via `yaha_client_config_set_dns_resolver`;
///  2. otherwise the platform resolver (`getaddrinfo`, off-thread);
///  3. if that fails or yields nothing, the last successful answer for the host, provided it is
///     within the configured fallback window.
///
/// Successful lookups always refresh the cache, whichever resolver produced them.
#[derive(Clone)]
pub struct YahaResolver {
    handler: Option<(OnResolveDns, NonZeroIsize)>,
    platform: GaiResolver,
    cache: Option<Arc<DnsCache>>,
}

impl YahaResolver {
    pub fn new(
        handler: Option<(OnResolveDns, NonZeroIsize)>,
        cache_fallback_duration: Option<Duration>,
    ) -> Self {
        YahaResolver {
            handler,
            platform: GaiResolver::new(),
            cache: cache_fallback_duration
                .filter(|d| !d.is_zero())
                .map(|d| Arc::new(DnsCache::new(d))),
        }
    }
}

type ResolveFuture =
    Pin<Box<dyn Future<Output = Result<std::vec::IntoIter<SocketAddr>, io::Error>> + Send>>;

impl Service<Name> for YahaResolver {
    type Response = std::vec::IntoIter<SocketAddr>;
    type Error = io::Error;
    type Future = ResolveFuture;

    fn poll_ready(&mut self, _cx: &mut task::Context<'_>) -> Poll<Result<(), io::Error>> {
        Poll::Ready(Ok(()))
    }

    fn call(&mut self, name: Name) -> Self::Future {
        let handler = self.handler;
        let cache = self.cache.clone();
        let mut platform = self.platform.clone();
        let host = name.as_str().to_string();

        Box::pin(async move {
            let resolved = match handler {
                Some((handler, state)) => {
                    // The managed resolver is expected to block - on Android it goes through JNI
                    // into `Network.getAllByName` - so keep it off the async worker threads, the
                    // same way `GaiResolver` treats `getaddrinfo`.
                    let host = host.clone();
                    tokio::task::spawn_blocking(move || invoke_handler(handler, state, &host))
                        .await
                        .unwrap_or_else(|join_error| {
                            Err(io::Error::new(io::ErrorKind::Other, join_error))
                        })
                }
                None => platform
                    .call(name)
                    .await
                    .map(|addresses| addresses.collect::<Vec<_>>()),
            };

            let error = match resolved {
                Ok(addresses) if !addresses.is_empty() => {
                    if let Some(cache) = &cache {
                        cache.store(&host, &addresses);
                    }
                    return Ok(addresses.into_iter());
                }
                Ok(_) => io::Error::new(
                    io::ErrorKind::NotFound,
                    format!("failed to lookup address information for '{host}': no addresses returned"),
                ),
                Err(error) => error,
            };

            // The lookup failed. Serving a stale address is strictly better than failing outright:
            // if the address is genuinely dead the connect attempt fails anyway, and if the failure
            // was the platform resolver losing its network (the Android background case) the old
            // address is almost certainly still correct.
            match cache.as_ref().and_then(|cache| cache.get(&host)) {
                Some(addresses) => Ok(addresses.into_iter()),
                None => Err(error),
            }
        })
    }
}

fn invoke_handler(
    handler: OnResolveDns,
    state: NonZeroIsize,
    host: &str,
) -> io::Result<Vec<SocketAddr>> {
    let mut buffer = [YahaSocketAddress::default(); YAHA_MAX_RESOLVED_ADDRESSES];

    let count = handler(
        state,
        host.as_ptr(),
        host.len(),
        buffer.as_mut_ptr(),
        YAHA_MAX_RESOLVED_ADDRESSES as i32,
    );

    if count < 0 {
        return Err(io::Error::new(
            io::ErrorKind::Other,
            format!(
                "failed to lookup address information for '{host}': the external name resolver reported a failure"
            ),
        ));
    }

    // Defend against a managed resolver reporting more than it was given room for.
    let count = (count as usize).min(YAHA_MAX_RESOLVED_ADDRESSES);

    Ok(buffer[..count]
        .iter()
        .filter_map(|entry| entry.to_socket_addr())
        .collect())
}
