# Khai biểu mẫu An toàn dữ liệu trên Google Play

> **Chưa dùng tới.** Hiện chưa có kế hoạch phát hành `com.mediahub.mobile` lên Google Play.
> Tài liệu này giữ lại để dùng khi nào có kế hoạch, và **sẽ cập nhật sau** — số phiên bản,
> danh sách quyền và các mục trong biểu mẫu đều có thể đã đổi so với lúc viết (04/09/2026).

Bảng tra để điền mục **Chính sách > An toàn dữ liệu** trong Play Console cho
`com.mediahub.mobile`. Nội dung phải khớp với trang công bố
[data-safety.html](./data-safety.html) — Google đối chiếu hai bên, lệch nhau là bị từ chối.

## Link phải khai

| Trường | Giá trị |
|---|---|
| Privacy policy URL | `https://mittohoa.github.io/media-tool/privacy.html` |
| Trang giới thiệu (Website) | `https://mittohoa.github.io/media-tool/` |

## Phần "Thu thập và chia sẻ dữ liệu"

**Câu mở đầu — "Ứng dụng của bạn có thu thập hoặc chia sẻ bất kỳ loại dữ liệu người dùng
bắt buộc nào không?"** → **Có**.

Không chọn "Không". Ứng dụng đúng là không gửi gì về máy chủ của chúng tôi, nhưng tính năng
AI trực tuyến *có* chuyển ảnh sang bên thứ ba. Google coi đó là **chia sẻ** trừ khi rơi trọn
vào ngoại lệ "người dùng tự khởi xướng", mà ngoại lệ đó chỉ an toàn khi mọi lần chuyển đều
có xác nhận rõ ràng. Khai "Có" rồi mô tả đúng phạm vi thì không mất gì; khai "Không" mà
Google tìm thấy lệnh gọi ra `api.openai.com` trong bản build là bị gỡ ứng dụng.

### Các loại dữ liệu cần đánh dấu

Chỉ ba dòng dưới đây được đánh dấu, mọi loại còn lại để trống.

| Loại dữ liệu | Thu thập | Chia sẻ | Mục đích | Bắt buộc? | Xử lý tạm thời? |
|---|---|---|---|---|---|
| Ảnh (Photos) | Không | **Có** | Chức năng ứng dụng | Người dùng chọn được | Không |
| Video | Không | **Có** | Chức năng ứng dụng | Người dùng chọn được | Không |
| Tệp và tài liệu | Không | Không | Chức năng ứng dụng | Người dùng chọn được | — |

Với mỗi dòng "Chia sẻ = Có", phần mô tả nêu rõ: chỉ khi người dùng bật tính năng AI trực
tuyến và tự cung cấp khoá API; dữ liệu đi thẳng tới nhà cung cấp mà người dùng chọn.

### KHÔNG đánh dấu những loại này

Vị trí (app không xin quyền vị trí), Danh tính cá nhân, Thông tin tài chính, Sức khoẻ,
Tin nhắn, Danh bạ, Lịch, Lịch sử tìm kiếm trong ứng dụng, Lịch sử duyệt web, Ứng dụng đã
cài, Hoạt động trong ứng dụng, Hiệu năng ứng dụng (không có báo cáo sự cố), Định danh thiết
bị, Định danh quảng cáo.

> Lưu ý về EXIF GPS: toạ độ nằm sẵn trong tệp ảnh của người dùng chứ không do ứng dụng thu
> thập. Ứng dụng không khai báo quyền `ACCESS_FINE_LOCATION` hay `ACCESS_COARSE_LOCATION`,
> nên không đánh dấu mục Vị trí. Toạ độ chỉ đi ra ngoài kèm theo chính bức ảnh mà người dùng
> gửi cho AI, và điều đó đã nằm trong dòng "Ảnh".

## Phần "Biện pháp bảo mật"

| Câu hỏi | Trả lời |
|---|---|
| Mã hoá khi truyền (encryption in transit) | **Có** — toàn bộ dùng HTTPS |
| Người dùng yêu cầu xoá dữ liệu được không | **Có** |
| URL yêu cầu xoá dữ liệu | `https://mittohoa.github.io/media-tool/privacy.html#xoa` |
| Đã qua kiểm định bảo mật độc lập | **Không** |
| Tuân thủ Chính sách gia đình | Ứng dụng không nhắm tới trẻ em |

## Khai báo quyền nhạy cảm

Ba quyền dưới đây Play sẽ hỏi riêng, phải có lời giải thích:

- **`REQUEST_INSTALL_PACKAGES`** — dùng cho tính năng tự cập nhật khi cài ngoài cửa hàng.
  **Nếu phát hành qua Play thì nên gỡ hẳn quyền này và tắt bộ cập nhật trong app.** Play cấm
  ứng dụng tự cập nhật bằng cơ chế riêng, và quyền này là lý do bị từ chối rất phổ biến.
  Bản trên Play nên để Play lo việc cập nhật.
- **`READ_MEDIA_IMAGES` / `READ_MEDIA_VIDEO` / `READ_MEDIA_AUDIO`** — chức năng cốt lõi là
  trình quản lý thư viện media. Cân nhắc hỗ trợ thêm chế độ ảnh được chọn
  (`READ_MEDIA_VISUAL_USER_SELECTED`) — ứng dụng đã khai quyền này rồi.
- **`CAMERA`** — chỉ dùng để quét mã QR, xin quyền ngay tại thời điểm mở tính năng.

## Trước khi nộp, kiểm lại

- [ ] Đã bật GitHub Pages và mở được `https://mittohoa.github.io/media-tool/privacy.html`
- [ ] Trang chính sách nêu đúng tên gói `com.mediahub.mobile`
- [ ] Đã điền email liên hệ thật vào cả `privacy.html` và `data-safety.html`
- [ ] Nội dung khai trong Play Console khớp từng dòng với `data-safety.html`
- [ ] Đã quyết định xử lý `REQUEST_INSTALL_PACKAGES` cho bản lên Play
- [ ] Ký bằng khoá phát hành thật, không phải debug key
