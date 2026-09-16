# Production call-recording archive

The production image-based compose configuration uses the existing external
Docker volume `orbita_crm_recordings_ats`. Its authoritative files live on PBX
at `/srv/orbita/crm-call-recordings`, reached from CRM over WireGuard:

- PBX private address: `10.77.0.1`.
- CRM private address: `10.77.0.2`.
- API path (unchanged): `/app/Data/crm/call-recordings`.

Only the archive's location changes. API authorization, call IDs, recording
filenames, upload processing and AI workers still use the same storage service.
Do not expose recording URLs or NFS exports publicly. Restrict the NFS export
to the CRM WireGuard peer, and protect access with the private network/firewall.

## Provisioning on the CRM host

First confirm the WireGuard peer and NFS export are reachable. Before creating
a volume, inspect any existing volume of this name: never recreate or delete an
existing volume to force an options change.

```sh
docker volume create --driver local \
  --opt type=nfs \
  --opt device=:/srv/orbita/crm-call-recordings \
  --opt o=addr=10.77.0.1,nfsvers=4,proto=tcp,rw,hard,timeo=600,retrans=2 \
  orbita_crm_recordings_ats
```

The explicit external volume fails closed if it has not been provisioned. The
NFS driver must not be replaced by an ordinary local volume as a fallback.
An unavailable PBX/VPN can block recording reads/writes and API startup. Restore
the private-network/storage path instead of silently creating a second archive.

## Migration and rollback

1. Compare same-named finalized `.bin` files by content before copying. Stop if
   content differs; preserve both versions for investigation.
2. Copy missing files to PBX without overwriting and without `--delete`.
3. Disable any old CRM-to-PBX mirror with deletion: the former local volume is
   incomplete and must not be allowed to erase the authoritative PBX archive.
4. Back up compose configuration, pause API only during the final catch-up,
   verify content, and recreate only API. Asterisk does not need a restart.
5. Verify the actual NFS mount inside API, references from the database,
   authenticated playback, new recording delivery and existing AI processing.
6. Keep the old local volume offline for rollback until the change is accepted.
   Do not delete original recordings as part of deployment.

If reverting later, copy any recordings created on PBX after the cutover into
the rollback archive first, with the same no-overwrite/no-delete checks. Merely
switching to the frozen old local volume would hide those newer recordings.
Never re-enable the old deleting mirror during rollback.

An independent backup and a retention policy are still necessary; a single
live archive is not a backup strategy.
