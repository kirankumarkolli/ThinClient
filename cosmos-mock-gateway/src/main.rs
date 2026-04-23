mod handlers;
mod models;
mod partition;
mod server;
mod state;

use clap::Parser;
use serde_json::json;
use std::collections::HashMap;
use std::net::SocketAddr;
use std::sync::Arc;
use tracing_subscriber::{fmt, EnvFilter};

use partition::generate_pkranges_json;
use state::AppState;

#[derive(Parser, Debug)]
#[command(name = "cosmos-mock-gateway")]
struct Args {
    #[arg(long, default_value_t = 8901)]
    port: u16,

    #[arg(long, default_value_t = 1)]
    partitions: u32,

    #[arg(long, default_value = "testdb")]
    db: String,

    #[arg(long, default_value = "testcoll")]
    container: String,

    #[arg(long, default_value = "/pk")]
    partition_key_path: String,

    #[arg(long, default_value = "info")]
    log_level: String,
}

#[tokio::main]
async fn main() {
    let args = Args::parse();

    fmt()
        .with_env_filter(
            EnvFilter::try_from_default_env()
                .unwrap_or_else(|_| EnvFilter::new(&args.log_level)),
        )
        .init();

    let db_rid = "KwdHAA==".to_string();
    let coll_rid = "KwdHANkV-KY=".to_string();
    let lsn: u64 = 100;

    let pkranges_json = generate_pkranges_json(args.partitions, &coll_rid, &db_rid, lsn);

    let mut documents = HashMap::new();
    documents.insert(
        ("test-pk".to_string(), "item-1".to_string()),
        json!({
            "id": "item-1",
            "pk": "test-pk",
            "data": "hello world"
        }),
    );

    let state = Arc::new(AppState {
        port: args.port,
        db_name: args.db,
        container_name: args.container,
        partition_key_path: args.partition_key_path,
        db_rid,
        coll_rid,
        coll_etag: "00000000-0000-0000-0000-000000000001".to_string(),
        pkranges_json,
        pkranges_etag: "100".to_string(),
        partition_count: args.partitions,
        documents,
        lsn,
    });

    let app = server::build_router(state.clone());

    // Generate self-signed TLS cert
    let subject_alt_names = vec!["localhost".to_string()];
    let cert = rcgen::generate_simple_self_signed(subject_alt_names).unwrap();
    let cert_der = cert.cert.der().clone();
    let key_der = cert.key_pair.serialize_der();

    let rustls_config = axum_server::tls_rustls::RustlsConfig::from_der(
        vec![cert_der.to_vec()],
        key_der.to_vec(),
    )
    .await
    .unwrap();

    let addr = SocketAddr::from(([0, 0, 0, 0], state.port));
    tracing::info!(
        "Mock Cosmos Gateway listening on https://localhost:{} with {} partitions",
        state.port,
        state.partition_count
    );

    axum_server::bind_rustls(addr, rustls_config)
        .serve(app.into_make_service())
        .await
        .unwrap();
}
