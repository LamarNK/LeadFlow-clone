-- Run only after:
-- 1. a fresh PostgreSQL backup;
-- 2. two complete worker cycles with CandidatePhoneWatches populated;
-- 3. zero new technical phone-watch duplicates during those cycles.
\set ON_ERROR_STOP on

BEGIN;

CREATE TEMP TABLE phone_watch_cleanup_map ON COMMIT DROP AS
SELECT
    legacy."Id" AS legacy_response_id,
    watch."CanonicalResponseId" AS canonical_response_id
FROM "CandidateResponses" legacy
JOIN "CandidatePhoneWatches" watch
  ON watch."AccountId" = legacy."AccountId"
 AND watch."AvitoSubProfileId" = legacy."AvitoSubProfileId"
 AND watch."PersonId" = legacy."PersonId"
WHERE legacy."Status" = 'Duplicate'
  AND legacy."SourceResponseId" LIKE 'phone-watch:%'
  AND watch."CanonicalResponseId" IS NOT NULL
  AND watch."CanonicalResponseId" <> legacy."Id";

UPDATE "CandidatePhoneHistory" history
SET "ResponseId" = map.canonical_response_id
FROM phone_watch_cleanup_map map
WHERE history."ResponseId" = map.legacy_response_id;

UPDATE "ResponseBitrixDeliveries" delivery
SET "ResponseId" = map.canonical_response_id
FROM phone_watch_cleanup_map map
WHERE delivery."ResponseId" = map.legacy_response_id;

UPDATE "ResponseCrmDeliveries" delivery
SET "ResponseId" = map.canonical_response_id
FROM phone_watch_cleanup_map map
WHERE delivery."ResponseId" = map.legacy_response_id;

UPDATE "CrmCandidateCards" card
SET "ResponseId" = map.canonical_response_id
FROM phone_watch_cleanup_map map
WHERE card."ResponseId" = map.legacy_response_id
  AND NOT EXISTS (
      SELECT 1
      FROM "CrmCandidateCards" existing
      WHERE existing."ResponseId" = map.canonical_response_id
  );

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM phone_watch_cleanup_map map
        JOIN "CrmCandidateCards" card ON card."ResponseId" = map.legacy_response_id
    ) THEN
        RAISE EXCEPTION 'Cleanup stopped: a technical response still owns a CRM card.';
    END IF;
END
$$;

DELETE FROM "CandidateResponses" response
USING phone_watch_cleanup_map map
WHERE response."Id" = map.legacy_response_id;

COMMIT;
