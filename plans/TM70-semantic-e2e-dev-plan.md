# Kế hoạch thực thi đã được Developer cho phép

1. Baseline develop sạch, tests không bật provider; lưu TRX ngoài repo.
2. TDD bounded search orchestration: lexical/semantic/RRF, exact count/page, freshness/failure.
3. SQL snapshot reader riêng, SQL lexical classes và business filters; không sửa independent reader/policy cũ.
4. Endpoint dev opt-in + key, DI và timeout; positive/negative tests.
5. Compose project mới, SQL fixture mới và read-only principal, index CLI; kiểm tra danh tính trước ghi.
6. Web/Mobile worktree riêng: canonical client, filters, states; review compliance rồi quality theo AGENTS.
7. Chạy HTTP E2E và dependency/freshness/security regressions, full tests/build phù hợp; một báo cáo tổng hợp và local commits. Không push/CI/production.
