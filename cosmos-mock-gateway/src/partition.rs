use serde_json::{json, Value};
use std::time::{SystemTime, UNIX_EPOCH};

pub fn generate_pkranges_json(
    partition_count: u32,
    coll_rid: &str,
    db_rid: &str,
    lsn: u64,
) -> String {
    let ts = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap()
        .as_secs();

    let n = partition_count as u128;
    let step = u128::MAX / n;

    let mut ranges: Vec<Value> = Vec::with_capacity(partition_count as usize);

    for i in 0..partition_count {
        let ii = i as u128;

        let min_inclusive = if i == 0 {
            String::new()
        } else {
            format!("{:032X}", ii * step)
        };

        let max_exclusive = if i == partition_count - 1 {
            "FF".repeat(16)
        } else {
            format!("{:032X}", (ii + 1) * step)
        };

        let rid = format!("{}CAAAAAAAAU{}==", coll_rid.trim_end_matches('='), i);

        ranges.push(json!({
            "id": i.to_string(),
            "_rid": rid,
            "_etag": format!("\"00000000-0000-0000-0000-{:012X}\"", i),
            "minInclusive": min_inclusive,
            "maxExclusive": max_exclusive,
            "ridPrefix": i,
            "_self": format!("dbs/{}/colls/{}/pkranges/{}/", db_rid, coll_rid, rid),
            "throughputFraction": 1,
            "status": "online",
            "parents": [],
            "lsn": lsn,
            "_lsn": lsn,
            "_ts": ts
        }));
    }

    let feed = json!({
        "_rid": coll_rid,
        "_count": partition_count,
        "PartitionKeyRanges": ranges
    });

    serde_json::to_string(&feed).unwrap()
}
