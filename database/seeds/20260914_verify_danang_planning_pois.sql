SET NOCOUNT ON;

SELECT COUNT(*) AS planning_ready_poi_count
FROM catalog.POIs
WHERE source_url IS NOT NULL
  AND verified_at IS NOT NULL
  AND avg_visit_duration_minutes > 0;

SELECT poi.name, COUNT(*) AS opening_hour_count
FROM catalog.POIs AS poi
INNER JOIN catalog.POIOpeningHours AS opening_hour ON opening_hour.poi_id = poi.poi_id
WHERE poi.source_url IS NOT NULL
  AND poi.verified_at IS NOT NULL
GROUP BY poi.name
HAVING COUNT(*) <> 7;
