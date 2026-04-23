use axum::{
    extract::State,
    http::{HeaderMap, StatusCode},
    response::IntoResponse,
    Json,
};
use std::sync::Arc;

use crate::models::{
    AccountProperties, ConsistencyPolicy, LocationEntry, ReadPolicy, ReplicationPolicy,
};
use crate::state::AppState;

pub async fn handle_account(State(state): State<Arc<AppState>>) -> impl IntoResponse {
    let endpoint = format!("https://localhost:{}/", state.port);

    let account = AccountProperties {
        self_link: String::new(),
        id: "localhost".to_string(),
        rid: "localhost".to_string(),
        media: "//media/".to_string(),
        addresses: "//addresses/".to_string(),
        dbs: "//dbs/".to_string(),
        writable_locations: vec![LocationEntry {
            name: "South Central US".to_string(),
            database_account_endpoint: endpoint.clone(),
        }],
        readable_locations: vec![LocationEntry {
            name: "South Central US".to_string(),
            database_account_endpoint: endpoint,
        }],
        enable_multiple_write_locations: false,
        user_replication_policy: ReplicationPolicy {
            async_replication: Some(false),
            min_replica_set_size: 1,
            max_replicaset_size: 4,
        },
        user_consistency_policy: ConsistencyPolicy {
            default_consistency_level: "Session".to_string(),
        },
        system_replication_policy: ReplicationPolicy {
            async_replication: None,
            min_replica_set_size: 1,
            max_replicaset_size: 4,
        },
        read_policy: ReadPolicy {
            primary_read_coefficient: 1,
            secondary_read_coefficient: 1,
        },
        query_engine_configuration: r#"{"maxSqlQueryInputLength":524288,"maxJoinsPerSqlQuery":10,"maxQueryRequestTimeoutFraction":0.9,"maxUdfRefPerSqlQuery":10,"spatialMaxGeometryPointCount":256,"sqlAllowTop":true,"enableSpatialIndexing":true,"maxInExpressionItemsCount":2147483647,"sqlAllowGroupByClause":true,"maxLogicalOrPerSqlQuery":2147483647,"maxLogicalAndPerSqlQuery":2147483647,"maxSpatialQueryCells":2147483647,"sqlAllowSubQuery":true,"sqlAllowScalarSubQuery":true,"allowNewKeywords":true,"sqlAllowLike":true,"sqlAllowNonFiniteNumbers":false,"sqlDisableOptimizationFlags":0,"queryMaxInMemorySortDocumentCount":-1000,"sqlAllowAggregateFunctions":true}"#.to_string(),
    };

    let mut headers = HeaderMap::new();
    headers.insert("content-type", "application/json".parse().unwrap());
    headers.insert(
        "x-ms-max-media-storage-usage-mb",
        "10240".parse().unwrap(),
    );
    headers.insert("x-ms-media-storage-usage-mb", "0".parse().unwrap());
    headers.insert("x-ms-databaseaccount-consumed-mb", "0".parse().unwrap());
    headers.insert("x-ms-databaseaccount-reserved-mb", "0".parse().unwrap());
    headers.insert(
        "x-ms-databaseaccount-provisioned-mb",
        "0".parse().unwrap(),
    );
    headers.insert(
        "x-ms-gatewayversion",
        "version=2.14.0".parse().unwrap(),
    );

    (StatusCode::OK, headers, Json(account))
}
