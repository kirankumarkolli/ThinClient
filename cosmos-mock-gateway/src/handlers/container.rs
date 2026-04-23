use axum::{
    extract::{Path, State},
    http::{HeaderMap, HeaderValue, StatusCode},
    response::IntoResponse,
    Json,
};
use std::sync::Arc;
use std::time::{SystemTime, UNIX_EPOCH};
use uuid::Uuid;

use crate::models::{
    ConflictResolutionPolicy, ContainerProperties, ErrorResponse, GeospatialConfig,
    IndexingPolicy, PartitionKeyDef, PathSpec,
};
use crate::state::AppState;

fn rfc1123_now() -> String {
    let now = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap()
        .as_secs();
    // Simple static-ish date; real RFC1123 formatting
    let _ = now;
    "Sun, 01 Jun 2025 00:00:00 GMT".to_string()
}

fn get_activity_id(req_headers: &HeaderMap) -> String {
    req_headers
        .get("x-ms-activity-id")
        .and_then(|v| v.to_str().ok())
        .map(|s| s.to_string())
        .unwrap_or_else(|| Uuid::new_v4().to_string())
}

pub async fn handle_container(
    State(state): State<Arc<AppState>>,
    Path((db, coll)): Path<(String, String)>,
    req_headers: HeaderMap,
) -> impl IntoResponse {
    if db != state.db_name || coll != state.container_name {
        let err = ErrorResponse {
            code: "NotFound".to_string(),
            message: format!("Entity with the specified id does not exist in the system."),
        };
        let mut headers = HeaderMap::new();
        headers.insert("x-ms-request-charge", "1".parse().unwrap());
        let activity_id = get_activity_id(&req_headers);
        headers.insert("x-ms-activity-id", activity_id.parse().unwrap());
        return (StatusCode::NOT_FOUND, headers, Json(serde_json::to_value(err).unwrap()));
    }

    let activity_id = get_activity_id(&req_headers);
    let ts = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap()
        .as_secs();

    let container = ContainerProperties {
        id: state.container_name.clone(),
        rid: state.coll_rid.clone(),
        self_link: format!("dbs/{}/colls/{}/", state.db_rid, state.coll_rid),
        etag: format!("\"{}\"", state.coll_etag),
        ts,
        partition_key: PartitionKeyDef {
            paths: vec![state.partition_key_path.clone()],
            kind: "Hash".to_string(),
        },
        indexing_policy: IndexingPolicy {
            indexing_mode: "consistent".to_string(),
            automatic: true,
            included_paths: vec![PathSpec {
                path: "/*".to_string(),
            }],
            excluded_paths: vec![PathSpec {
                path: "/\"_etag\"/?".to_string(),
            }],
        },
        conflict_resolution_policy: ConflictResolutionPolicy {
            mode: "LastWriterWins".to_string(),
            conflict_resolution_path: "/_ts".to_string(),
            conflict_resolution_procedure: String::new(),
        },
        geospatial_config: GeospatialConfig {
            geo_type: "Geography".to_string(),
        },
        docs: "docs/".to_string(),
        sprocs: "sprocs/".to_string(),
        triggers: "triggers/".to_string(),
        udfs: "udfs/".to_string(),
        conflicts: "conflicts/".to_string(),
    };

    let session_token = format!("0:-1#{}", state.lsn);

    let mut headers = HeaderMap::new();
    headers.insert("content-type", "application/json".parse().unwrap());
    headers.insert("etag", HeaderValue::from_str(&format!("\"{}\"", state.coll_etag)).unwrap());
    headers.insert("x-ms-activity-id", activity_id.parse().unwrap());
    headers.insert("x-ms-content-path", state.db_rid.parse().unwrap());
    headers.insert("x-ms-session-token", session_token.parse().unwrap());
    headers.insert("x-ms-request-charge", "2".parse().unwrap());
    headers.insert(
        "x-ms-alt-content-path",
        format!("dbs/{}", state.db_name).parse().unwrap(),
    );
    headers.insert("lsn", state.lsn.to_string().parse().unwrap());
    headers.insert(
        "x-ms-last-state-change-utc",
        rfc1123_now().parse().unwrap(),
    );
    headers.insert("x-ms-schemaversion", "1.21".parse().unwrap());
    headers.insert("x-ms-gatewayversion", "version=2.14.0".parse().unwrap());

    (StatusCode::OK, headers, Json(serde_json::to_value(container).unwrap()))
}
