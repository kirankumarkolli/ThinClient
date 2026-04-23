use std::collections::HashMap;
use serde_json::Value;

pub struct AppState {
    pub port: u16,
    pub db_name: String,
    pub container_name: String,
    pub partition_key_path: String,
    pub db_rid: String,
    pub coll_rid: String,
    pub coll_etag: String,
    pub pkranges_json: String,
    pub pkranges_etag: String,
    pub partition_count: u32,
    pub documents: HashMap<(String, String), Value>,
    pub lsn: u64,
}
