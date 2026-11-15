-- Review these active POIs before enabling rain-only alerts for them.
-- An existing default 'Outdoor' value is not evidence of review.
SELECT poi_id, name, indoor_outdoor, latitude, longitude
FROM catalog.POIs
WHERE status = 'Active'
  AND indoor_outdoor_reviewed_at_utc IS NULL
ORDER BY poi_id;

SELECT COUNT_BIG(*) AS unreviewed_active_pois
FROM catalog.POIs
WHERE status = 'Active'
  AND indoor_outdoor_reviewed_at_utc IS NULL;
