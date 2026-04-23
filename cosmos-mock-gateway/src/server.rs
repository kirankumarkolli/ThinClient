use axum::{routing::get, Router};
use std::sync::Arc;
use tower_http::trace::TraceLayer;

use crate::handlers::{account, container, document, pkranges};
use crate::state::AppState;

pub fn build_router(state: Arc<AppState>) -> Router {
    Router::new()
        .route("/", get(account::handle_account))
        .route("/dbs/{db}/colls/{coll}", get(container::handle_container))
        .route(
            "/dbs/{db_rid}/colls/{coll_rid}/pkranges",
            get(pkranges::handle_pkranges),
        )
        .route(
            "/dbs/{db}/colls/{coll}/docs/{id}",
            get(document::handle_document),
        )
        .with_state(state)
        .layer(TraceLayer::new_for_http())
}
