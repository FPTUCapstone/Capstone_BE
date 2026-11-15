/* UC-10 (ADR-002, G2): record how each generated itinerary's schedule was produced.
   Additive and nullable: itineraries saved before this migration keep NULL provenance; no inferred backfill. */
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'planning.Itineraries', N'U') IS NULL
    THROW 51110, 'planning.Itineraries must exist before applying the UC-10 provenance migration.', 1;

IF COL_LENGTH(N'planning.Itineraries', N'solver_outcome') IS NULL
    ALTER TABLE planning.Itineraries ADD solver_outcome VARCHAR(64) NULL;

IF COL_LENGTH(N'planning.Itineraries', N'objective_mode') IS NULL
    ALTER TABLE planning.Itineraries ADD objective_mode VARCHAR(20) NULL;

IF COL_LENGTH(N'planning.Itineraries', N'search_completed') IS NULL
    ALTER TABLE planning.Itineraries ADD search_completed BIT NULL;

IF COL_LENGTH(N'planning.Itineraries', N'time_of_day_refined') IS NULL
    ALTER TABLE planning.Itineraries ADD time_of_day_refined BIT NULL;

COMMIT TRANSACTION;
