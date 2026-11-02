# WORKFLOW: viết tài liệu dài bằng nhiều agent trong Claude Code

Bộ file này dùng cho tài liệu dài dựa trên chat về lab microservice. Nó giải quyết hai vấn đề đã gặp: phiên Opus chạy chậm và hết token giữa chừng, và nhiều agent viết song song dễ lệch giọng, lệch thuật ngữ.

**Ngôn ngữ**: các file dành cho Claude Code (`CLAUDE.md`, agent, lệnh, script, brief, `PROGRESS.md`, `REVIEW.md`, báo cáo) viết bằng tiếng Anh để agent hiểu chính xác. Nội dung tài liệu cuối cùng (`docs/sections/`, `GLOSSARY.md`, `INDEX.md`) viết bằng tiếng Việt, và các lệnh báo cáo lại cho bạn bằng tiếng Việt. File này và `docs/source/README.md` là dành cho bạn nên cũng bằng tiếng Việt.

## 1. Ý tưởng

| Bước | Lệnh | Agent / model | Vì sao chọn như vậy |
|------|------|---------------|---------------------|
| 1. Lập khung | `/plan-doc` | `doc-architect` (Opus) | Quyết định khó nhất và rẻ nhất để sửa. Chỉ chạy một lần, đầu ra ngắn. |
| 2. Viết | `/write-sections` | `doc-writer` (Sonnet), tối đa 3 song song | Phần nặng token nhất. Sonnet nhanh hơn và rẻ hơn Opus, đủ tốt cho văn giải thích và code. |
| 3. Cơ học | `/polish-doc` | `doc-polisher` (Haiku) | Glossary, mục lục, kiểm tra thuật ngữ và link. Không cần phán đoán. |
| 4. Review | `/review-doc` | `doc-reviewer` (Opus) | Cần đọc xuyên suốt và bắt mâu thuẫn giữa các section. Chạy trong phiên riêng nên không tự chấm bài của mình. |
| 5. Sửa | `/fix-review` | `doc-writer` (Sonnet) | Sửa tối thiểu theo từng mục review. |
| 6. Xuất | `bash scripts/build.sh` | không dùng model | Gộp section, xuất docx/pdf nếu có pandoc. |

Ba nguyên tắc giữ cả quy trình ổn định:

1. **Hợp đồng trước, nội dung sau.** `00-brief.md` và `lab-facts.md` là thứ duy nhất mà các writer chia sẻ. Writer không thấy section của nhau nên mọi thứ cần thống nhất (thuật ngữ, ví dụ xuyên suốt, quy ước code) phải nằm trong brief.
2. **Trạng thái nằm trong file section.** Mỗi writer chỉ ghi vào file của mình, nên chạy song song không ghi đè lẫn nhau. Hết token giữa chừng thì chỉ mất một phần của section đang viết.
3. **Mỗi phiên một việc.** Phiên điều phối không đọc lại nội dung đã viết, nên context nhỏ, nhanh, và rẻ.

## 2. Cài đặt (một lần)

1. Copy toàn bộ bộ file này vào gốc repo lab:
   ```bash
   cp -r doc-kit/. /đường/dẫn/lab-ecommerce/
   ```
   Nếu repo đã có `CLAUDE.md` hoặc `.claude/settings.json`, hãy gộp nội dung vào thay vì ghi đè.
2. Thêm vào `.gitignore`:
   ```
   docs/dist/
   docs/_snippets/**/bin/
   docs/_snippets/**/obj/
   ```
3. Đặt nguồn vào `docs/source/lab-plan.md`. Hướng dẫn chi tiết ở `docs/source/README.md`.
4. Mở Claude Code tại gốc repo và gõ `/agents`. Bạn phải thấy 4 agent: `doc-architect`, `doc-writer`, `doc-polisher`, `doc-reviewer`. Gõ `/` để kiểm tra 5 lệnh: `plan-doc`, `write-sections`, `polish-doc`, `review-doc`, `fix-review`.
5. Kiểm tra script: `bash scripts/status.sh` (chưa có section thì chỉ in tiêu đề và TOTAL 0).
6. Nếu bạn muốn build thử code, cần có .NET SDK trên máy chạy Claude Code. Thiếu thì writer sẽ đánh dấu `UNVERIFIED`, không giả vờ đã chạy.

## 3. Chạy từng bước

### Bước 1: lập khung
```
/plan-doc Tài liệu hướng dẫn implement và giải thích kiến trúc lab e-commerce microservices, cho dev .NET 3-5 năm, khoảng 15.000-20.000 từ, tiếng Việt
```
Sửa phần mô tả cho đúng ý bạn: loại tài liệu (hướng dẫn từng bước, hay giải thích kiến trúc và lý do chọn pattern), đối tượng đọc, độ dài.

Sau đó **tự đọc và duyệt** hai file:
- `docs/00-brief.md` (tiếng Anh): mục "Assumptions and open questions" là chỗ cần bạn quyết định. Kiểm tra bảng thuật ngữ (`Use | Avoid`) và ví dụ xuyên suốt (running example).
- `docs/PROGRESS.md`: kiểm tra danh sách section, ngân sách, phụ thuộc. Gộp, tách, đổi thứ tự trực tiếp ở đây.

Đừng bỏ qua bước duyệt. Lỗi ở khung sẽ nhân lên ở mọi section.

### Bước 2: viết, lặp theo đợt
```
/clear
/write-sections
```
Mỗi lần chạy xử lý tối đa 3 section sẵn sàng. Lặp `/clear` rồi `/write-sections` đến khi báo hết section. Muốn chọn cụ thể: `/write-sections S03 S05`.

Giữa các đợt, đọc thử một section để chắc giọng văn đúng ý. Nếu lệch, sửa `00-brief.md` (không sửa từng section) rồi chạy lại các section còn lại.

### Bước 3: polish
```
/clear
/polish-doc
```
Đọc `docs/POLISH-REPORT.md`, xử lý mục "Needs a human decision".

### Bước 4: review, trong phiên mới
```
/clear
/review-doc
```
Đọc `docs/REVIEW.md` (tiếng Anh; mỗi mục có mức P0/P1/P2, heading, vấn đề và cách sửa). Mục nào bạn không đồng ý thì xóa dòng đó trước khi sửa.

### Bước 5: sửa
```
/clear
/fix-review
```
Mặc định chỉ xử lý P0 và P1. Muốn cả P2: `/fix-review --all`. Xong P0/P1 mà thay đổi lớn thì chạy `/review-doc` lần nữa.

### Bước 6: xuất
```bash
bash scripts/build.sh
```
Kết quả nằm ở `docs/dist/`. Cần pandoc để có docx. PDF cần thêm engine LaTeX. Nếu thiếu, bạn có thể đưa `doc.md` vào Claude app để tạo doc hoặc xuất file.

## 4. Khi phiên bị ngắt hoặc hết token

Bạn không cần nhớ mình đang ở đâu:

- Chạy `bash scripts/status.sh`. Cột STATUS cho biết `draft` (viết dở), `done`, `needs-fix` hay `missing`.
- Chạy lại `/write-sections`. Section `draft` được ưu tiên làm tiếp. Writer đọc file dở và viết tiếp phần còn thiếu thay vì làm lại từ đầu, vì mỗi mục đã được lưu ngay khi viết xong.
- `/fix-review` cũng chạy lại an toàn: writer kiểm tra từng lỗi còn tồn tại không trước khi sửa.
- Hết token của gói thì chờ đến khi quota làm mới, hoặc đổi sang model nhẹ hơn bằng `/model` rồi chạy lại cùng lệnh. Không mất gì.

## 5. Giảm chậm và tốn token

- **Đừng để Opus viết phần thân tài liệu.** Opus chỉ ở `/plan-doc` và `/review-doc`. Các lệnh còn lại đã đặt `model:` hoặc gọi subagent có model riêng.
- **`/clear` giữa các phiên.** Brief và file trạng thái đã chứa đủ ngữ cảnh, nên không cần mang lịch sử theo.
- **Đừng dán chat lab vào prompt.** Nguồn nằm trong `lab-plan.md` và writer chỉ đọc đoạn ghi ở cột nguồn. Nếu `lab-plan.md` quá dài, hãy chia thành vài file trong `docs/source/` và ghi tên file vào cột nguồn.
- **Nếu bản Claude Code của bạn có tùy chọn mức effort**, đặt thấp hoặc trung bình cho `/write-sections`, `/polish-doc`, `/fix-review`. Giữ cao hơn cho `/plan-doc` và `/review-doc`.
- **Tối đa 3 writer song song.** Nhiều hơn dễ chạm giới hạn tốc độ và không nhanh hơn đáng kể.
- Muốn biết token đang đi đâu, dùng `/context` trong Claude Code, hoặc nhờ Claude giải thích mức sử dụng.

## 6. Tùy biến

- **Đổi model**: sửa dòng `model:` trong từng file `.claude/agents/*.md`. Review quan trọng thì ghim phiên bản cụ thể thay vì alias.
- **Tài liệu có nhiều code hơn**: thêm vào brief mục quy ước code chi tiết hơn, và đặt thêm một agent `code-checker` (Sonnet, có Bash) chỉ chạy build, test các snippet.
- **Thêm ngôn ngữ**: thêm một bước dịch sau `/fix-review` bằng writer, với bảng thuật ngữ cho ngôn ngữ đích trong brief.
- **Tài liệu nhỏ (dưới khoảng 15–20 trang)**: bỏ quy trình này, một phiên Sonnet viết liền mạch sẽ nhất quán hơn.

## 7. Những điều chưa kiểm chứng

Tôi viết bộ file này theo cấu trúc Claude Code thông dụng (agent là file Markdown có front matter `name`, `description`, `tools`, `model`; lệnh là file trong `.claude/commands/`; quyền trong `.claude/settings.json`). Tôi chưa chạy nó trong Claude Code thật. Khi chạy lần đầu hãy kiểm tra:

- `/agents` hiện đủ 4 agent. Nếu thiếu, kiểm tra front matter (dòng `---`, không có ký tự lạ).
- Lệnh slash hiện đủ 5 lệnh và `model:` trong front matter được áp dụng. Nếu phiên bản của bạn bỏ qua `model:`, dùng `/model` chọn trước khi chạy.
- Các quy tắc `permissions` không chặn nhầm việc bạn cần. Prompt xin quyền hiện ra thì chọn cho phép và sửa `settings.json` cho phù hợp.
- `doc-writer` thật sự chạy song song 3 section trong cùng lượt. Nếu chạy tuần tự thì vẫn đúng, chỉ chậm hơn.

Phát hiện chỗ nào lệch, quay lại chat này, tôi chỉnh bộ file cho khớp.
