use serde::Serialize;

#[derive(Serialize)]
pub struct AccountProperties {
    #[serde(rename = "_self")]
    pub self_link: String,
    pub id: String,
    #[serde(rename = "_rid")]
    pub rid: String,
    pub media: String,
    pub addresses: String,
    #[serde(rename = "_dbs")]
    pub dbs: String,
    #[serde(rename = "writableLocations")]
    pub writable_locations: Vec<LocationEntry>,
    #[serde(rename = "readableLocations")]
    pub readable_locations: Vec<LocationEntry>,
    #[serde(rename = "enableMultipleWriteLocations")]
    pub enable_multiple_write_locations: bool,
    #[serde(rename = "userReplicationPolicy")]
    pub user_replication_policy: ReplicationPolicy,
    #[serde(rename = "userConsistencyPolicy")]
    pub user_consistency_policy: ConsistencyPolicy,
    #[serde(rename = "systemReplicationPolicy")]
    pub system_replication_policy: ReplicationPolicy,
    #[serde(rename = "readPolicy")]
    pub read_policy: ReadPolicy,
    #[serde(rename = "queryEngineConfiguration")]
    pub query_engine_configuration: String,
}

#[derive(Serialize)]
pub struct LocationEntry {
    pub name: String,
    #[serde(rename = "databaseAccountEndpoint")]
    pub database_account_endpoint: String,
}

#[derive(Serialize)]
pub struct ReplicationPolicy {
    #[serde(rename = "asyncReplication", skip_serializing_if = "Option::is_none")]
    pub async_replication: Option<bool>,
    #[serde(rename = "minReplicaSetSize")]
    pub min_replica_set_size: u32,
    #[serde(rename = "maxReplicasetSize")]
    pub max_replicaset_size: u32,
}

#[derive(Serialize)]
pub struct ConsistencyPolicy {
    #[serde(rename = "defaultConsistencyLevel")]
    pub default_consistency_level: String,
}

#[derive(Serialize)]
pub struct ReadPolicy {
    #[serde(rename = "primaryReadCoefficient")]
    pub primary_read_coefficient: u32,
    #[serde(rename = "secondaryReadCoefficient")]
    pub secondary_read_coefficient: u32,
}

#[derive(Serialize)]
pub struct ContainerProperties {
    pub id: String,
    #[serde(rename = "_rid")]
    pub rid: String,
    #[serde(rename = "_self")]
    pub self_link: String,
    #[serde(rename = "_etag")]
    pub etag: String,
    #[serde(rename = "_ts")]
    pub ts: u64,
    #[serde(rename = "partitionKey")]
    pub partition_key: PartitionKeyDef,
    #[serde(rename = "indexingPolicy")]
    pub indexing_policy: IndexingPolicy,
    #[serde(rename = "conflictResolutionPolicy")]
    pub conflict_resolution_policy: ConflictResolutionPolicy,
    #[serde(rename = "geospatialConfig")]
    pub geospatial_config: GeospatialConfig,
    #[serde(rename = "_docs")]
    pub docs: String,
    #[serde(rename = "_sprocs")]
    pub sprocs: String,
    #[serde(rename = "_triggers")]
    pub triggers: String,
    #[serde(rename = "_udfs")]
    pub udfs: String,
    #[serde(rename = "_conflicts")]
    pub conflicts: String,
}

#[derive(Serialize)]
pub struct PartitionKeyDef {
    pub paths: Vec<String>,
    pub kind: String,
}

#[derive(Serialize)]
pub struct IndexingPolicy {
    #[serde(rename = "indexingMode")]
    pub indexing_mode: String,
    pub automatic: bool,
    #[serde(rename = "includedPaths")]
    pub included_paths: Vec<PathSpec>,
    #[serde(rename = "excludedPaths")]
    pub excluded_paths: Vec<PathSpec>,
}

#[derive(Serialize)]
pub struct PathSpec {
    pub path: String,
}

#[derive(Serialize)]
pub struct ConflictResolutionPolicy {
    pub mode: String,
    #[serde(rename = "conflictResolutionPath")]
    pub conflict_resolution_path: String,
    #[serde(rename = "conflictResolutionProcedure")]
    pub conflict_resolution_procedure: String,
}

#[derive(Serialize)]
pub struct GeospatialConfig {
    #[serde(rename = "type")]
    pub geo_type: String,
}

#[derive(Serialize)]
pub struct ErrorResponse {
    pub code: String,
    pub message: String,
}
