/*
    UC-30 development-only catalog. Dockerfile.seeded executes this script;
    canonical schema and runtime migrations never do.
*/
IF NOT EXISTS (
    SELECT 1
    FROM commercial.ServiceProviders
    WHERE name = N'Da Nang Ride' AND service_category = 'Vehicle')
    INSERT INTO commercial.ServiceProviders (name, service_category, contact_email, contact_phone, status)
    VALUES (N'Da Nang Ride', 'Vehicle', N'hello@example.test', N'02363555555', 'Active');

IF NOT EXISTS (
    SELECT 1
    FROM commercial.ServiceProviders
    WHERE name = N'Hai Chau Stay' AND service_category = 'Hotel')
    INSERT INTO commercial.ServiceProviders (name, service_category, contact_email, contact_phone, status)
    VALUES (N'Hai Chau Stay', 'Hotel', N'hello@example.test', N'02363666666', 'Active');

IF NOT EXISTS (
    SELECT 1
    FROM commercial.ServiceProviders
    WHERE name = N'Veranda Da Nang' AND service_category = 'Restaurant')
    INSERT INTO commercial.ServiceProviders (name, service_category, contact_email, contact_phone, status)
    VALUES (N'Veranda Da Nang', 'Restaurant', N'hello@example.test', N'02363777777', 'Active');

INSERT INTO commercial.Services
    (provider_id, service_category, name, description, price_amount, price_unit, capacity,
     attributes_json, availability_status, currency_code, price_includes_tax,
     refundable_deposit_amount, fulfilment_location_label, pickup_or_arrival_instructions,
     cancellation_policy_summary)
SELECT provider_id, 'Vehicle', N'Honda Wave 110cc', N'Local development catalog scooter rental.',
       180000.00, 'PerDay', 2, N'{"transmission":"Automatic","seats":2,"licenseRequired":true}',
       'Available', 'VND', 1, 500000.00, N'Da Nang Ride — 25 Tran Phu, Hai Chau',
       N'Bring a valid driving licence at pickup.', N'Cancellation terms are confirmed before booking.'
FROM (
    SELECT TOP (1) provider_id
    FROM commercial.ServiceProviders
    WHERE name = N'Da Nang Ride' AND service_category = 'Vehicle'
    ORDER BY provider_id
) AS provider
WHERE NOT EXISTS (
    SELECT 1
    FROM commercial.Services
    WHERE provider_id = provider.provider_id
      AND service_category = 'Vehicle'
      AND name = N'Honda Wave 110cc');

INSERT INTO commercial.Services
    (provider_id, service_category, name, description, price_amount, price_unit, capacity,
     attributes_json, availability_status, currency_code, price_includes_tax,
     refundable_deposit_amount, fulfilment_location_label, pickup_or_arrival_instructions,
     cancellation_policy_summary)
SELECT provider_id, 'Hotel', N'Deluxe River View Room', N'Local development catalog hotel room.',
       950000.00, 'PerNight', 2, N'{"roomType":"Deluxe","beds":1,"checkInTime":"14:00","checkOutTime":"12:00"}',
       'Available', 'VND', 1, NULL, N'Hai Chau Stay — Hai Chau, Da Nang',
       N'Present your booking confirmation at check-in.', N'Cancellation terms are confirmed before booking.'
FROM (
    SELECT TOP (1) provider_id
    FROM commercial.ServiceProviders
    WHERE name = N'Hai Chau Stay' AND service_category = 'Hotel'
    ORDER BY provider_id
) AS provider
WHERE NOT EXISTS (
    SELECT 1
    FROM commercial.Services
    WHERE provider_id = provider.provider_id
      AND service_category = 'Hotel'
      AND name = N'Deluxe River View Room');

INSERT INTO commercial.Services
    (provider_id, service_category, name, description, price_amount, price_unit, capacity,
     attributes_json, availability_status, currency_code, price_includes_tax,
     refundable_deposit_amount, fulfilment_location_label, pickup_or_arrival_instructions,
     cancellation_policy_summary)
SELECT provider_id, 'Restaurant', N'Vietnamese Set Menu for Two', N'Local development catalog restaurant offer.',
       420000.00, 'PerItem', 2, N'{"cuisine":"Vietnamese","servingSize":"2 people","reservationType":"Table request"}',
       'Available', 'VND', 1, NULL, N'Veranda Da Nang — Bach Dang, Hai Chau',
       N'Please arrive within 15 minutes of the confirmed reservation time.',
       N'Cancellation terms are confirmed before booking.'
FROM (
    SELECT TOP (1) provider_id
    FROM commercial.ServiceProviders
    WHERE name = N'Veranda Da Nang' AND service_category = 'Restaurant'
    ORDER BY provider_id
) AS provider
WHERE NOT EXISTS (
    SELECT 1
    FROM commercial.Services
    WHERE provider_id = provider.provider_id
      AND service_category = 'Restaurant'
      AND name = N'Vietnamese Set Menu for Two');
GO
