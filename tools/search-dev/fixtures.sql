SET NOCOUNT ON;
INSERT catalog.TourCategories(code,name,is_active) VALUES ('dev-culture',N'Culture (dev fixture)',1),('dev-beach',N'Beach (dev fixture)',1),('dev-inactive',N'Inactive fixture',0);
SET IDENTITY_INSERT dbo.Users ON;
INSERT dbo.Users(user_id,role,email,full_name,status) VALUES (901,'TourOperator',N'semantic-operator@example.test',N'Disposable Operator','Active'),(902,'Traveler',N'semantic-traveler@example.test',N'Disposable Traveler','Active');
SET IDENTITY_INSERT dbo.Users OFF;
INSERT dbo.OperatorProfiles(user_id,company_name,tax_code,business_license_no,approval_status) VALUES(901,N'Dev Travel',N'DEV-SEMANTIC-901',N'DEV-SEMANTIC-LIC','Approved');
SET IDENTITY_INSERT commerce.Tours ON;
INSERT commerce.Tours(tour_id,operator_user_id,title,base_price,duration_days,status,published_at,category_id)
VALUES (1,901,N'Khám phá phố cổ Hội An',1200000,2,'Approved','2026-01-01',(SELECT category_id FROM catalog.TourCategories WHERE code='dev-culture')),
(2,901,N'Biển Đà Nẵng nghỉ dưỡng',2400000,3,'Published','2026-01-01',(SELECT category_id FROM catalog.TourCategories WHERE code='dev-beach')),
(3,901,N'Di sản cố đô Huế',900000,1,'Approved','2026-01-01',(SELECT category_id FROM catalog.TourCategories WHERE code='dev-culture')),
(4,901,N'Hidden Draft Hội An',1000,2,'Draft',NULL,NULL),
(5,901,N'Unpublished Hội An',1000,2,'Approved',NULL,NULL),
(6,901,N'Past completed discovery',400000,1,'Published','2026-01-01',NULL);
SET IDENTITY_INSERT commerce.Tours OFF;
INSERT catalog.Destinations(name) VALUES(N'Hội An'),(N'Đà Nẵng'),(N'Huế');
INSERT commerce.TourDestinations(tour_id,destination_id,sequence_no) SELECT 1,destination_id,1 FROM catalog.Destinations WHERE name=N'Hội An';
INSERT commerce.TourDestinations(tour_id,destination_id,sequence_no) SELECT 2,destination_id,1 FROM catalog.Destinations WHERE name=N'Đà Nẵng';
INSERT commerce.TourDestinations(tour_id,destination_id,sequence_no) SELECT 3,destination_id,1 FROM catalog.Destinations WHERE name=N'Huế';
INSERT commerce.TourSchedules(tour_id,start_datetime,end_datetime,total_capacity,reserved_capacity,status)
VALUES(1,'2026-12-10T02:00:00','2026-12-12T02:00:00',10,0,'Scheduled'),
(2,'2026-12-11T02:00:00','2026-12-14T02:00:00',10,10,'Scheduled'),
(3,'2026-12-12T02:00:00','2026-12-13T02:00:00',10,2,'Scheduled'),
(6,'2026-01-01T02:00:00','2026-01-02T02:00:00',10,3,'Completed');
INSERT social.Reviews(traveler_user_id,target_type,target_id,rating) VALUES(902,'Tour',1,4),(902,'Tour',1,5),(902,'Tour',2,3),(902,'Tour',3,5);
