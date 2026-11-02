# docs/source: nguồn của tài liệu

Thư mục này chỉ chứa nguồn. Claude Code bị chặn ghi vào đây (xem `.claude/settings.json`), nên bạn tự đặt file vào.

## Cần có

`lab-plan.md`: toàn bộ nội dung lab mà tài liệu dựa vào (kế hoạch implement, các track và phase, danh sách pattern, kịch bản flash sale, dữ liệu mẫu...). Giữ nguyên các heading để `doc-architect` tham chiếu được theo heading.

## Cách đưa chat về lab vào đây

Claude Code không đọc được chat trên claude.ai. Chọn một trong hai cách:

1. **Xuất doc**: mở doc "Lab: E-commerce microservices" được tạo trong chat, xuất ra Markdown rồi lưu thành `docs/source/lab-plan.md`.
2. **Nhờ Claude trong chat**: mở chat "E-commerce microservice architecture blueprint" và nhờ ghi toàn bộ nội dung kế hoạch (kể cả phần trao đổi về plan v2, lịch, pattern) thành một file Markdown để tải về.

Với cả hai cách, hãy kiểm tra file có đủ các phần bạn muốn đưa vào tài liệu. Cái gì không có trong `lab-plan.md` thì tài liệu sẽ không có, và writer sẽ ghi vào `docs/gaps/` thay vì bịa.

## Nên có thêm (tùy chọn)

- Đoạn trích hoặc link tới code thật của lab (`src/...`), để writer trích code từ file thật.
- Ghi chú quyết định kiến trúc (ADR) nếu có.
