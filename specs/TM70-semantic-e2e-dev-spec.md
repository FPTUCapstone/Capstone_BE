# TM-70 Search E2E dev — phạm vi đã duyệt

Developer duyệt ngày 2026-10-17: endpoint riêng `/api/dev/tours/search`, canonical V2 + `mode=lexical|semantic|hybrid`, không thay legacy. Chỉ Development/Staging, explicit opt-in + khóa dev riêng; không production. Category endpoint `/api/dev/tours/categories` giữ DTO taxonomy hiện hành.

SQL quyết định visibility và mọi filter; raw rating dùng reader hiện hành. Toàn bộ public corpus tối đa 256 Tour, SQL provider quyết định lexical membership/classes và Title/Id ties. Semantic không threshold; hybrid RRF k cấu hình thử nghiệm mặc định 60. Không suy luận filter từ câu tự nhiên. Thiếu/stale/wrong-model vector, dependency lỗi hoặc corpus quá lớn trả 503 ProblemDetails, không fallback/truncate/giả totals. No-keyword browse không cần vector. Một asOfUtc cho mỗi request.

Giữ nguyên safety tests/fixtures, migration, PR92, Track B. SQL dev là container/volume mới; API dùng SELECT-only principal. Indexing riêng, chỉ public Title/Destinations/Operator, pinned E5 384 chiều; collection mới có tên riêng, không xóa collection cũ.

Nghiệm thu: tests ranking/filter/count/failure/access gates; real embedding/Qdrant/SQL và Web/Mobile request; phân biệt UI/device chưa chạy. SEM-D1–D5 và official E2 gates giữ nguyên.
