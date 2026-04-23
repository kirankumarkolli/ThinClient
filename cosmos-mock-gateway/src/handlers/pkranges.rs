use axum::{
    extract::{Path, State},
    http::{HeaderMap, HeaderValue, StatusCode},
    response::IntoResponse,
};
use std::sync::Arc;
use uuid::Uuid;

use crate::state::AppState;

fn rfc1123_now() -> String {
    "Sun, 01 Jun 2025 00:00:00 GMT".to_string()
}

fn get_activity_id(req_headers: &HeaderMap) -> String {
    req_headers
        .get("x-ms-activity-id")
        .and_then(|v| v.to_str().ok())
        .map(|s| s.to_string())
        .unwrap_or_else(|| Uuid::new_v4().to_string())
}

pub async fn handle_pkranges(
    State(state): State<Arc<AppState>>,
    Path((_db_rid, _coll_rid)): Path<(String, String)>,
    req_headers: HeaderMap,
) -> impl IntoResponse {
    let activity_id = get_activity_id(&req_headers);
    let session_token = format!("0:-1#{}", state.lsn);

    // Check If-None-Match
    let if_none_match = req_headers
        .get("if-none-match")
        .and_then(|v| v.to_str().ok())
        .map(|s| s.trim_matches('"').to_string());

    let is_not_modified =
        if_none_match.as_deref() == Some(&state.pkranges_etag);

    let mut headers = HeaderMap::new();
    headers.insert(
        "etag",
        HeaderValue::from_str(&format!("\"{}\"", state.pkranges_etag)).unwrap(),
    );
    headers.insert("x-ms-activity-id", activity_id.parse().unwrap());
    headers.insert("x-ms-session-token", session_token.parse().unwrap());
    headers.insert(
        "x-ms-last-state-change-utc",
        rfc1123_now().parse().unwrap(),
    );
    headers.insert("x-ms-schemaversion", "1.21".parse().unwrap());
    headers.insert("x-ms-gatewayversion", "version=2.14.0".parse().unwrap());
    headers.insert("lsn", state.lsn.to_string().parse().unwrap());
    headers.insert("content-type", "application/json".parse().unwrap());

    if is_not_modified {
        headers.insert("x-ms-item-count", "0".parse().unwrap());
        (StatusCode::NOT_MODIFIED, headers, String::new())
    } else {
        headers.insert(
            "x-ms-item-count",
            state.partition_count.to_string().parse().unwrap(),
        );
        (StatusCode::OK, headers, state.pkranges_json.clone())
    }
}
