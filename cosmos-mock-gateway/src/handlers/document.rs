use axum::{
    extract::{Path, State},
    http::{HeaderMap, StatusCode},
    response::IntoResponse,
    Json,
};
use serde_json::Value;
use std::collections::hash_map::DefaultHasher;
use std::hash::{Hash, Hasher};
use std::sync::Arc;
use std::time::{SystemTime, UNIX_EPOCH};
use uuid::Uuid;

use crate::models::ErrorResponse;
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

pub async fn handle_document(
    State(state): State<Arc<AppState>>,
    Path((db, coll, id)): Path<(String, String, String)>,
    req_headers: HeaderMap,
) -> impl IntoResponse {
    let activity_id = get_activity_id(&req_headers);

    if db != state.db_name || coll != state.container_name {
        let err = ErrorResponse {
            code: "NotFound".to_string(),
            message: "Entity with the specified id does not exist in the system.".to_string(),
        };
        let mut headers = HeaderMap::new();
        headers.insert("x-ms-request-charge", "1".parse().unwrap());
        headers.insert("x-ms-activity-id", activity_id.parse().unwrap());
        return (StatusCode::NOT_FOUND, headers, Json(serde_json::to_value(err).unwrap()));
    }

    // Parse partition key from header
    let pk = req_headers
        .get("x-ms-documentdb-partitionkey")
        .and_then(|v| v.to_str().ok())
        .and_then(|s| {
            let arr: Result<Vec<Value>, _> = serde_json::from_str(s);
            arr.ok()
        })
        .and_then(|arr| {
            arr.first().and_then(|v| match v {
                Value::String(s) => Some(s.clone()),
                other => Some(other.to_string()),
            })
        })
        .unwrap_or_default();

    let key = (pk.clone(), id.clone());

    match state.documents.get(&key) {
        Some(doc) => {
            let ts = SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_secs();

            let mut hasher = DefaultHasher::new();
            id.hash(&mut hasher);
            pk.hash(&mut hasher);
            let hash_val = hasher.finish();

            let doc_rid = format!("{}BAAAAAAAAAA==", state.coll_rid.trim_end_matches('='));
            let doc_etag = format!("00000000-0000-0000-0000-{:012X}", hash_val & 0xFFFFFFFFFFFF);

            let mut doc_clone = doc.clone();
            if let Some(obj) = doc_clone.as_object_mut() {
                obj.insert("_rid".to_string(), Value::String(doc_rid.clone()));
                obj.insert(
                    "_self".to_string(),
                    Value::String(format!(
                        "dbs/{}/colls/{}/docs/{}/",
                        state.db_rid, state.coll_rid, doc_rid
                    )),
                );
                obj.insert(
                    "_etag".to_string(),
                    Value::String(format!("\"{}\"", doc_etag)),
                );
                obj.insert("_ts".to_string(), Value::Number(ts.into()));
                obj.insert(
                    "_attachments".to_string(),
                    Value::String("attachments/".to_string()),
                );
            }

            let session_token = format!("0:-1#{}", state.lsn);

            let mut headers = HeaderMap::new();
            headers.insert("content-type", "application/json".parse().unwrap());
            headers.insert("etag", format!("\"{}\"", doc_etag).parse().unwrap());
            headers.insert("x-ms-request-charge", "1".parse().unwrap());
            headers.insert("x-ms-session-token", session_token.parse().unwrap());
            headers.insert("x-ms-activity-id", activity_id.parse().unwrap());
            headers.insert("x-ms-documentdb-partitionkeyrangeid", "0".parse().unwrap());
            headers.insert(
                "x-ms-alt-content-path",
                format!("dbs/{}/colls/{}", state.db_name, state.container_name)
                    .parse()
                    .unwrap(),
            );
            headers.insert("x-ms-content-path", state.coll_rid.parse().unwrap());
            headers.insert("lsn", state.lsn.to_string().parse().unwrap());
            headers.insert("x-ms-item-lsn", state.lsn.to_string().parse().unwrap());
            headers.insert(
                "x-ms-last-state-change-utc",
                rfc1123_now().parse().unwrap(),
            );
            headers.insert("x-ms-schemaversion", "1.21".parse().unwrap());
            headers.insert("x-ms-gatewayversion", "version=2.14.0".parse().unwrap());

            (StatusCode::OK, headers, Json(doc_clone))
        }
        None => {
            let err = ErrorResponse {
                code: "NotFound".to_string(),
                message: "Entity with the specified id does not exist in the system.".to_string(),
            };
            let mut headers = HeaderMap::new();
            headers.insert("x-ms-request-charge", "1".parse().unwrap());
            headers.insert("x-ms-activity-id", activity_id.parse().unwrap());
            (StatusCode::NOT_FOUND, headers, Json(serde_json::to_value(err).unwrap()))
        }
    }
}
