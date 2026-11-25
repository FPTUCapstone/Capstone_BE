# UC-10 issue #117 implementation plan

1. Reproduce alias search, zero-minute sunrise, and missing candidate traits with targeted unit tests.
2. Use the canonical alias list in EF queries and `IsFoodAndDrink` in in-memory planning and weighted inputs; verify search, suggestion, cost, and mandatory tests.
3. Configure and validate ordinary/sunrise minutes and early cutoff; verify refiner tests.
4. Carry POI indoor/outdoor and tag names into create and versioned candidates; verify mapping tests.
5. Run `dotnet format TripMate.slnx --verify-no-changes --no-restore --include` for changed C# files, regenerate the source-hash benchmark, and run Application and Infrastructure unit suites.
