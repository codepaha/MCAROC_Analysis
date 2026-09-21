# Operational Runbook: Company Master Registry Shared Snapshot Storage

This document outlines the operational deployment, storage architecture, preflight verification, and retention maintenance for the **Company Master Registry Shared Snapshot Store** (`IRegistrySnapshotStore`).

---

## 1. Storage Architecture & Multi-Node Scale Safety

The Company Master registry contains over 3.7 million records (and up to 30 million in production). Rebuilding verified aggregate metrics requires nine grouped whole-table scans. To prevent a stampeding herd of concurrent full-table scans across multiple portal instances when caches are cold, the registry employs a two-tier caching and distributed coordination model:

1. **L1 Fast In-Memory Cache (`IMemoryCache`)**: Process-local, sub-millisecond access for active portal requests.
2. **L2 Durable Shared Snapshot Store (`FileRegistrySnapshotStore`)**: Persisted JSON snapshot envelopes stored on a shared filesystem volume accessible to all portal instances.
3. **Distributed SQL Application Locks (`sp_getapplock`)**:
   - `Shared` lock on `"CompanyMaster_PromotionAdmission"` guarantees mutual exclusion with active delta promotions.
   - `Exclusive` lock on `"CompanyMaster_AggregateRebuild"` ensures only ONE node across the entire cluster can execute a rebuild at any time.

---

## 2. Shared Storage Requirements for Multi-Node Deployments

In multi-node production environments, portal instances do not share local disks (`App_Data`). Therefore, `RegistrySnapshotStore:Root` must be explicitly configured to point to a shared network filesystem volume (e.g., Azure Files, AWS EFS, or a dedicated SMB/NFS share mounted at an identical path on all nodes).

### Filesystem & Mount Requirements
1. **Same-Volume Atomic Rename**: Temporary files are created in the same root directory (`snapshot_{jobId}.tmp.{Guid:N}`) and atomically renamed via `File.Move(..., overwrite: true)`. The shared volume must support atomic directory renames on the same mount.
2. **Access Control (ACLs)**: The service account or container identity under which the portal runs must have:
   - `Read`
   - `Write`
   - `Delete` (for temporary file cleanup and retention pruning)
3. **Fail-Closed Production Preflight**: The application executes `FileRegistrySnapshotStore.ValidatePreflight` immediately after `builder.Build()` and before `app.Run()`. If `RegistrySnapshotStore:Root` is missing, empty, or inaccessible in production, the application terminates immediately during deployment.

---

## 3. Configuration & Preflight Verification

### Configuration Example (`appsettings.Production.json` or Environment Variables)
```json
{
  "RegistrySnapshotStore": {
    "Root": "/mnt/mcaroc-shared/registry-snapshots",
    "RetentionCount": 5
  }
}
```
Or via environment variable:
```bash
RegistrySnapshotStore__Root=/mnt/mcaroc-shared/registry-snapshots
RegistrySnapshotStore__RetentionCount=5
```

### Preflight Verification Steps
Before opening traffic to a newly deployed portal node, verify shared storage connectivity:

1. **Check Mount Availability**:
   ```bash
   ls -la /mnt/mcaroc-shared/registry-snapshots
   ```
2. **Verify Multi-Node Visibility**:
   - On Node 1, write a test canary:
     ```bash
     echo "preflight" > /mnt/mcaroc-shared/registry-snapshots/canary_test.txt
     ```
   - On Node 2, verify visibility:
     ```bash
     cat /mnt/mcaroc-shared/registry-snapshots/canary_test.txt
     ```
   - On Node 2, delete the canary:
     ```bash
     rm /mnt/mcaroc-shared/registry-snapshots/canary_test.txt
     ```
3. **Application Logs**: Verify that application startup logs:
   ```
   INFO: Successfully persisted verified aggregate snapshot envelope for Job <Id> at '<TargetFile>'
   ```

---

## 4. Retention & Maintenance

- **Automated Pruning**: On each successful snapshot write, `FileRegistrySnapshotStore` automatically enumerates `snapshot_*.json` files and deletes snapshots older than `RetentionCount` (default: 5 most recent jobs).
- **Temporary Files**: Active `.tmp.*` files are safely excluded from retention pruning to prevent deleting concurrent writes.
