use std::{
    num::NonZeroIsize,
    sync::{Arc, Mutex},
    time::Duration,
};
use futures_channel::mpsc::Sender;
use http_body_util::combinators::BoxBody;
use tokio::runtime::{Builder, Handle, Runtime};

use hyper::{
    body::Bytes, 
    Request, StatusCode
};

use hyper_util::{
    client::{self, legacy::{connect::HttpConnector, Client, ResponseFuture}},
    rt::{TokioExecutor, TokioTimer},
};

use hyper_rustls::ConfigBuilderExt;

#[cfg(feature = "rustls")]
use hyper_rustls::HttpsConnector;
#[cfg(feature = "native")]
use hyper_tls::HttpsConnector;
#[cfg(unix)]
use hyperlocal::UnixConnector;

use rustls::pki_types::{CertificateDer, PrivateKeyDer};
use tokio_util::sync::CancellationToken;

use crate::{
    primitives::{CompletionReason, YahaHttpVersion},
    resolver::{OnResolveDns, YahaResolver},
};

/// The `HttpConnector` type this library always builds: name resolution goes through
/// [`YahaResolver`] so that an external resolver and the stale-result fallback cache are available
/// regardless of whether either is configured.
pub type YahaHttpConnector = HttpConnector<YahaResolver>;

/// How long a cached address stays usable as a fallback after a failed lookup.
///
/// Deliberately generous. The cache is only consulted once a lookup has already failed, so the
/// alternative to a stale address is a hard error; and the case it exists for - an Android app
/// resuming from the background onto a stale network binding - can easily follow an overnight
/// pause. See `YetAnotherHttpHandler.DnsCacheFallbackDuration` to change or disable it.
pub const DEFAULT_DNS_CACHE_FALLBACK_DURATION: Duration = Duration::from_secs(24 * 60 * 60);

pub type OnStatusCodeAndHeadersReceive =
    extern "C" fn(req_seq: i32, state: NonZeroIsize, status_code: i32, version: YahaHttpVersion);
pub type OnReceive = extern "C" fn(req_seq: i32, state: NonZeroIsize, length: usize, buf: *const u8, task_handle: usize);
pub type OnComplete = extern "C" fn(req_seq: i32, state: NonZeroIsize, reason: CompletionReason, h2_error_code: u32);
type OnServerCertificateVerificationHandler = extern "C" fn(callback_state: NonZeroIsize, server_name: *const u8, server_name_len: usize, certificate_der: *const u8, certificate_der_len: usize, now: u64) -> bool;

pub struct YahaNativeRuntimeContext;
pub struct YahaNativeRuntimeContextInternal {
    pub runtime: Runtime
}

/// Name given to every tokio worker thread. The managed side matches on this exact string; see
/// `UnsafeUtilities.WorkerThreadName`. Must stay <= 15 bytes (Linux thread-name limit).
pub const WORKER_THREAD_NAME: &str = "yaha-rt-worker";

impl YahaNativeRuntimeContextInternal {
    pub fn from_raw_context(ctx: *mut YahaNativeRuntimeContext) -> &'static mut Self {
        unsafe { &mut *(ctx as *mut Self) }
    }
    pub fn new(worker_threads: i32) -> YahaNativeRuntimeContextInternal {
        let mut builder = Builder::new_multi_thread();
        let mut builder = builder.enable_all();

        // Name the worker threads explicitly instead of inheriting tokio's default, so that tests
        // (and anyone reading a thread dump) can identify them. Relying on tokio's default broke
        // silently when tokio renamed it from "tokio-runtime-worker" to "tokio-rt-worker"
        // (tokio #7880); pinning it here keeps that in one place across future tokio releases.
        //
        // NOTE: this name is no longer load-bearing for correctness. "Am I on a runtime thread?" is
        // answered by `yaha_is_runtime_thread`, which asks tokio rather than comparing strings.
        //
        // Keep this at 15 bytes or fewer: Linux truncates thread names at 15 (TASK_COMM_LEN - 1),
        // which is the very limit that prompted tokio's rename.
        builder = builder.thread_name(WORKER_THREAD_NAME);

        // Set the number of worker threads. If not larger than 0, use the default number of threads. (= number of cores)
        if worker_threads > 0 {
            builder = builder.worker_threads(worker_threads as usize);
        }

        YahaNativeRuntimeContextInternal {
            runtime: builder.build().unwrap(),
        }
    }
}

pub struct YahaNativeContext;
pub struct YahaNativeContextInternal<'a> {
    pub runtime: tokio::runtime::Handle,
    pub client_builder: Option<client::legacy::Builder>,
    pub skip_certificate_verification: Option<bool>,
    pub server_certificate_verification_handler: Option<(OnServerCertificateVerificationHandler, NonZeroIsize)>,
    pub root_certificates: Option<rustls::RootCertStore>,
    pub override_server_name: Option<String>,
    pub connect_timeout: Option<Duration>,
    pub client_auth_certificates: Option<Vec<CertificateDer<'a>>>,
    pub client_auth_key: Option<PrivateKeyDer<'a>>,
    pub tcp_client: Option<Client<HttpsConnector<YahaHttpConnector>, BoxBody<Bytes, hyper::Error>>>,
    pub dns_resolver_handler: Option<(OnResolveDns, NonZeroIsize)>,
    pub dns_cache_fallback_duration: Option<Duration>,
    pub on_status_code_and_headers_receive: OnStatusCodeAndHeadersReceive,
    pub on_receive: OnReceive,
    pub on_complete: OnComplete,

    #[cfg(unix)]
    pub uds_client: Option<Client<UnixConnector, BoxBody<Bytes, hyper::Error>>>,
    #[cfg(unix)]
    pub uds_socket_path: Option<std::path::PathBuf>,
}

impl YahaNativeContextInternal<'_> {
    pub fn from_raw_context(ctx: *mut YahaNativeContext) -> &'static mut Self {
        unsafe { &mut *(ctx as *mut Self) }
    }

    pub fn new(
        runtime_handle: Handle,
        on_status_code_and_headers_receive: OnStatusCodeAndHeadersReceive,
        on_receive: OnReceive,
        on_complete: OnComplete,
    ) -> Self {
        YahaNativeContextInternal {
            runtime: runtime_handle,
            tcp_client: None,
            client_builder: Some(Client::builder(TokioExecutor::new())),
            skip_certificate_verification: None,
            server_certificate_verification_handler: None,
            root_certificates: None,
            override_server_name: None,
            connect_timeout: None,
            dns_resolver_handler: None,
            // Enabled by default: it only ever takes effect once a lookup has already failed.
            // See `YetAnotherHttpHandler.DnsCacheFallbackDuration` for the rationale.
            dns_cache_fallback_duration: Some(DEFAULT_DNS_CACHE_FALLBACK_DURATION),
            client_auth_certificates: None,
            client_auth_key: None,
            on_status_code_and_headers_receive,
            on_receive,
            on_complete,
            #[cfg(unix)]
            uds_client: None,
            #[cfg(unix)]
            uds_socket_path: None,
        }
    }

    pub fn build_client(&mut self) {
        let mut builder = self.client_builder.take().unwrap();
        builder.timer(TokioTimer::new());

        #[cfg(unix)]
        {
            if self.uds_socket_path.is_some() {
                self.uds_client = Some(builder.build(UnixConnector));
            } else {
                let https = self.new_connector();
                self.tcp_client = Some(builder.build(https));
            }
        }
        #[cfg(not(unix))]
        {
            let https = self.new_connector();
            self.tcp_client = Some(builder.build(https));
        }
    }

    fn build_resolver(&self) -> YahaResolver {
        YahaResolver::new(self.dns_resolver_handler, self.dns_cache_fallback_duration)
    }

    #[cfg(feature = "rustls")]
    fn new_connector(&mut self) -> HttpsConnector<YahaHttpConnector> {
        let tls_config_builder = rustls::ClientConfig::builder();

        // Configure certificate root store.
        let tls_config: rustls::ClientConfig;
        if let Some(server_certificate_verification_handler) = self.server_certificate_verification_handler {
            // Use custom certificate verification handler
            tls_config = tls_config_builder
                .dangerous()
                .with_custom_certificate_verifier(Arc::new(danger::CustomCerficateVerification { handler: server_certificate_verification_handler }))
                .with_no_client_auth();
        } else if self.skip_certificate_verification.unwrap_or_default() {
            // Skip certificate verification
            tls_config = tls_config_builder
                .dangerous()
                .with_custom_certificate_verifier(Arc::new(danger::NoCertificateVerification{}))
                .with_no_client_auth();
        } else {
            // Configure to use built-in certification store and client authentication.
            let tls_config_builder_root: rustls::ConfigBuilder<
                rustls::ClientConfig,
                rustls::client::WantsClientCert,
            >;
            if let Some(root_certificates) = &self.root_certificates {
                tls_config_builder_root =
                    tls_config_builder.with_root_certificates(root_certificates.to_owned());
            } else {
                tls_config_builder_root = tls_config_builder.with_webpki_roots();
            }

            tls_config = if let Some(client_auth_certificates) = &self.client_auth_certificates {
                if let Some(client_auth_key) = &self.client_auth_key {
                    let certs: Vec<CertificateDer> = client_auth_certificates
                        .iter()
                        .map(|c| c.clone().into_owned())
                        .collect();

                    tls_config_builder_root
                        .clone()
                        .with_client_auth_cert(
                            certs,
                            client_auth_key.clone_key(),
                        )
                        .unwrap_or(tls_config_builder_root.with_no_client_auth())
                } else {
                    tls_config_builder_root.with_no_client_auth()
                }
            } else {
                tls_config_builder_root.with_no_client_auth()
            }
        }

        let builder = hyper_rustls::HttpsConnectorBuilder::new()
            .with_tls_config(tls_config)
            .https_or_http();

        let builder = if let Some(override_server_name) = &self.override_server_name {
            builder.with_server_name(override_server_name.clone())
        } else {
            builder
        };

        let builder = builder
            .enable_all_versions();

        // Almost the same as `builder.build()`, but specify `set_nodelay(true)` and our resolver.
        let mut http_conn = HttpConnector::new_with_resolver(self.build_resolver());
        http_conn.set_nodelay(true);
        http_conn.enforce_http(false);
        http_conn.set_connect_timeout(self.connect_timeout);
        builder.wrap_connector(http_conn)
    }

    #[cfg(feature = "native")]
    fn new_connector(&mut self, server_certificate_verification_handler: Option<OnServerCertificateVerificationHandler>) -> HttpsConnector<HttpConnector> {
        let https = HttpsConnector::new();
        https
    }

    /// Takes an owned snapshot of everything an in-flight request needs from this context.
    ///
    /// See [`RequestClient`]: the spawned request task must never borrow the context itself.
    pub fn request_client(&self) -> RequestClient {
        RequestClient {
            on_status_code_and_headers_receive: self.on_status_code_and_headers_receive,
            on_receive: self.on_receive,
            on_complete: self.on_complete,
            tcp_client: self.tcp_client.clone(),
            #[cfg(unix)]
            uds_client: self.uds_client.clone(),
            #[cfg(unix)]
            uds_socket_path: self.uds_socket_path.clone(),
        }
    }
}

/// An owned snapshot of the parts of [`YahaNativeContextInternal`] that a request needs while it
/// runs: the three managed callbacks and the hyper client.
///
/// The request task must hold one of these rather than a reference to the context. The managed side
/// can call `yaha_dispose_context` at any time, and that frees the context allocation outright, so a
/// `&'static mut YahaNativeContextInternal` living inside a spawned task would dangle - and reading
/// `on_complete` out of freed memory is exactly the kind of native->managed jump that corrupts the
/// managed heap. Everything here is cheap to clone: the callbacks are plain function pointers and
/// `Client` is internally reference-counted.
#[derive(Clone)]
pub struct RequestClient {
    pub on_status_code_and_headers_receive: OnStatusCodeAndHeadersReceive,
    pub on_receive: OnReceive,
    pub on_complete: OnComplete,

    tcp_client: Option<Client<HttpsConnector<YahaHttpConnector>, BoxBody<Bytes, hyper::Error>>>,

    #[cfg(unix)]
    uds_client: Option<Client<UnixConnector, BoxBody<Bytes, hyper::Error>>>,
    #[cfg(unix)]
    uds_socket_path: Option<std::path::PathBuf>,
}

impl RequestClient {
    /// Whether `yaha_build_client` has run. `yaha_request_begin` reports an error to the managed
    /// side rather than sending when this is false.
    pub fn has_client(&self) -> bool {
        #[cfg(unix)]
        {
            self.tcp_client.is_some() || self.uds_client.is_some()
        }
        #[cfg(not(unix))]
        {
            self.tcp_client.is_some()
        }
    }

    #[cfg(unix)]
    pub fn request(&self, mut req: Request<BoxBody<Bytes, hyper::Error>>) -> ResponseFuture {
        // Precondition (`uds_client` or `tcp_client` is set) ensured by `Self::has_client` and `yaha_request_begin`
        if let Some(uds_socket_path) = &self.uds_socket_path {
            // Transform HTTP URIs to the format expected by hyperlocal
            let path_and_query = req
                .uri()
                .path_and_query()
                .map(|pq| pq.as_str())
                .unwrap_or("/");
            let uds_uri = hyperlocal::Uri::new(uds_socket_path, path_and_query);
            *req.uri_mut() = uds_uri.into();

            self.uds_client.as_ref().unwrap().request(req)
        } else {
            self.tcp_client.as_ref().unwrap().request(req)
        }
    }
    #[cfg(not(unix))]
    pub fn request(&self, req: Request<BoxBody<Bytes, hyper::Error>>) -> ResponseFuture {
        self.tcp_client.as_ref().unwrap().request(req)
    }
}

#[cfg(feature = "rustls")]
mod danger {
    use std::num::NonZeroIsize;

    use rustls::client::danger::{HandshakeSignatureValid, ServerCertVerified};
    use rustls::{DigitallySignedStruct, Error, SignatureScheme};
    use rustls::pki_types::{CertificateDer, ServerName, UnixTime};

    use super::OnServerCertificateVerificationHandler;

    #[derive(Debug)]
    pub struct NoCertificateVerification {}

    #[derive(Debug)]
    pub struct CustomCerficateVerification {
        pub handler: (OnServerCertificateVerificationHandler, NonZeroIsize)
    }

    const ALL_SCHEMES: [SignatureScheme; 12] = [
        SignatureScheme::RSA_PKCS1_SHA1,
        SignatureScheme::ECDSA_SHA1_Legacy,
        SignatureScheme::RSA_PKCS1_SHA256,
        SignatureScheme::ECDSA_NISTP256_SHA256,
        SignatureScheme::RSA_PKCS1_SHA384,
        SignatureScheme::ECDSA_NISTP384_SHA384,
        SignatureScheme::ECDSA_NISTP521_SHA512,
        SignatureScheme::RSA_PSS_SHA256,
        SignatureScheme::RSA_PSS_SHA384,
        SignatureScheme::RSA_PSS_SHA512,
        SignatureScheme::ED25519,
        SignatureScheme::ED448];

    impl rustls::client::danger::ServerCertVerifier for CustomCerficateVerification {
        fn verify_server_cert(
            &self,
            end_entity: &CertificateDer<'_>,
            _intermediates: &[CertificateDer<'_>],
            server_name: &ServerName<'_>,
            _ocsp_response: &[u8],
            now: UnixTime,
        ) -> Result<ServerCertVerified, Error> {
            let server_name = server_name.to_str();
            let server_name = server_name.as_bytes();
            let cetificate_der = end_entity.as_ref();

            if (self.handler.0)(self.handler.1, server_name.as_ptr(), server_name.len(), cetificate_der.as_ptr(), cetificate_der.len(), now.as_secs()) {
                Ok(ServerCertVerified::assertion())
            } else {
                Err(Error::InvalidCertificate(rustls::CertificateError::ApplicationVerificationFailure))
            }
        }

        fn verify_tls12_signature(
            &self,
            _message: &[u8],
            _cert: &CertificateDer<'_>,
            _dss: &DigitallySignedStruct,
        ) -> Result<HandshakeSignatureValid, Error> {
            Ok(HandshakeSignatureValid::assertion())
        }

        fn verify_tls13_signature(
            &self,
            _message: &[u8],
            _cert: &CertificateDer<'_>,
            _dss: &DigitallySignedStruct,
        ) -> Result<HandshakeSignatureValid, Error> {
            Ok(HandshakeSignatureValid::assertion())
        }

        fn supported_verify_schemes(&self) -> Vec<SignatureScheme> {
            Vec::from(ALL_SCHEMES)
        }
    }

    impl rustls::client::danger::ServerCertVerifier for NoCertificateVerification {
        fn verify_server_cert(
            &self,
            _end_entity: &CertificateDer<'_>,
            _intermediates: &[CertificateDer<'_>],
            _server_name: &ServerName<'_>,
            _ocsp_response: &[u8],
            _now: UnixTime) -> Result<ServerCertVerified, Error> {
            Ok(ServerCertVerified::assertion())
        }

        fn verify_tls12_signature(
            &self,
            _message: &[u8],
            _cert: &CertificateDer<'_>,
            _dss: &DigitallySignedStruct) -> Result<HandshakeSignatureValid, Error> {
            Ok(HandshakeSignatureValid::assertion())
        }

        fn verify_tls13_signature(
            &self,
            _message: &[u8],
            _cert: &CertificateDer<'_>,
            _dss: &DigitallySignedStruct) -> Result<HandshakeSignatureValid, Error> {
            Ok(HandshakeSignatureValid::assertion())
        }



        fn supported_verify_schemes(&self) -> Vec<SignatureScheme> {
            Vec::from(ALL_SCHEMES)
        }
    }
}

pub struct YahaNativeRequestContext;
pub struct YahaNativeRequestContextInternal {
    pub seq: i32,
    pub builder: Option<hyper::http::request::Builder>,
    pub sender: Option<Sender<Bytes>>,
    pub has_body: bool,
    pub completed: bool,
    pub cancellation_token: CancellationToken,
    pub last_error: Option<String>,

    pub response_version: YahaHttpVersion,
    pub response_status: StatusCode,
    pub response_headers: Option<Vec<(String, String)>>,
    pub response_trailers: Option<Vec<(String, String)>>,
}

impl YahaNativeRequestContextInternal {
    pub fn try_complete(&mut self) {
        if self.sender.is_some() {
            // By dropping, the sending body channel is completed.
            self.sender = None;
        }
    }
}
impl Drop for YahaNativeRequestContextInternal {
    fn drop(&mut self) {
        //println!("YahaNativeRequestContextInternal.Drop");
    }
}

pub trait Internalizable<T> {}

impl Internalizable<Mutex<YahaNativeRequestContextInternal>> for YahaNativeRequestContext {}

pub fn to_internal<'a, T: Internalizable<U>, U>(v: *const T) -> &'a U {
    unsafe { &(*(v as *const U)) }
}
pub fn to_internal_arc<'a, T: Internalizable<U>, U>(v: *const T) -> Arc<U> {
    unsafe { Arc::from_raw(v as *const U) }
}
