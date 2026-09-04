# Chốt thuật toán và thư viện

Trả lời câu hỏi: dùng thuật toán gì, thư viện nào, có cần AI không.

Kết luận ngắn: **bài toán của bạn không cần AI.** Bốn tầng dưới đây, tầng càng cao càng
đắt, và tầng 2 mới là tầng giải quyết đúng yêu cầu "trùng nhưng thiếu metadata".

---

## Bốn tầng, không phải một thuật toán

| Tầng | Bắt được | Thuật toán | Chi phí | Sai sót |
|---|---|---|---|---|
| 1. Byte hash | Copy y hệt | xxHash128 sau cascade size→probe | Đã xong | 0 |
| **2. Pixel hash** | **Cùng ảnh, khác metadata** | Hash buffer pixel đã decode | 1 lần decode | **0** |
| 3. Perceptual hash | Resize, nén lại, đổi format | dHash + pHash 64-bit + MIH | Chung lượt decode với tầng 2 | Có ngưỡng, cần verify |
| 4. Embedding | Crop, watermark, chỉnh sửa nặng | CLIP/DINOv2 qua ONNX Runtime | Rất đắt | Nhiều |

---

## Tầng 2 là câu trả lời cho bài toán bạn nêu — và nó không phải pHash

Đây là điểm dễ bỏ sót nhất.

Xoá EXIF **không đụng một pixel nào**. Nó chỉ cắt bỏ khối metadata trong file. Nên nếu
decode hai file ra buffer pixel:

```
anh_goc.jpg      → [pixel buffer X] + EXIF đầy đủ
anh_da_strip.jpg → [pixel buffer X] + không EXIF
```

Buffer pixel **giống hệt nhau, từng byte một**. Vậy chỉ cần:

```
pixel_hash = xxHash128(buffer_pixel_sau_khi_decode)
```

→ khớp tuyệt đối, **không ngưỡng, không false positive, không cần AI, không cần pHash**.

Đây mới là thuật toán chính cho yêu cầu của bạn. pHash là tầng phụ, để bắt trường hợp
ảnh còn bị resize hoặc nén lại nữa.

**Điều kiện để tầng 2 đúng:** phải chuẩn hoá trước khi hash, nếu không sẽ ra false negative:
- Cố định color space, decode ra grayscale 8-bit ở kích thước cố định.
- Bỏ alpha channel nếu toàn bộ opaque.

**Và một điều KHÔNG được làm: đừng áp EXIF Orientation.**

Bản thiết kế đầu tiên của tôi ghi là phải áp — sai. Lý do: cờ Orientation *chính là*
metadata. Bản gốc có cờ = 6 (xoay 90°), bản bị strip mất cờ, còn cờ = 1. Nếu chuẩn hoá theo
cờ thì bản gốc bị xoay, bản strip giữ nguyên → **hai bản hoá ra không khớp**, đúng ngược
với mục đích.

Pixel thô mới là thứ hai bản thật sự chia sẻ, nên hash pixel thô. Việc chịu được ảnh xoay
để cho tầng 3 lo: pHash tính cả 4 hướng rồi lấy giá trị nhỏ nhất.

---

## Tầng 3: pHash — tự viết, đừng lấy thư viện

Bản thân thuật toán chỉ khoảng 50 dòng. Không đáng để thêm một dependency.

**dHash (64-bit)** — nhạy với resize/nén lại:
```
resize về 9×8 grayscale → so sánh pixel kề nhau theo hàng → 64 bit
```

**pHash (64-bit)** — bền hơn với thay đổi độ sáng/gamma:
```
resize về 32×32 grayscale → DCT-II 2 chiều → lấy khối 8×8 góc trên trái (bỏ hệ số DC)
→ so với median → 64 bit
```

Dùng **cả hai**, khớp một trong hai là thành ứng viên. Recall cao hơn hẳn dùng riêng lẻ.

**Ghép cặp bằng Multi-Index Hashing**, không phải so đôi một:

> Chia hash 64-bit thành `T+1` band. Hai hash cách nhau Hamming ≤ `T` thì **bắt buộc**
> trùng khít ít nhất 1 band (nguyên lý chuồng bồ câu).

Chọn **T = 4** → 5 band × 13 bit. T lớn hơn nghe có vẻ bắt được nhiều hơn nhưng thực tế
làm số band tăng, mỗi bucket phình ra, và MIH thoái hoá về gần O(N²). T=4 là điểm cân
bằng thực dụng; tăng recall bằng cách dùng 2 loại hash thay vì nới T.

**Bắt buộc có bước verify.** pHash chỉ chọn ứng viên, không kết luận. Vì vậy:

> **Lưu kèm thumbnail 16×16 grayscale (256 byte) vào catalog ngay trong lượt decode.**
> 2 triệu ảnh = 512 MB, hoàn toàn chấp nhận được.

Có nó rồi thì verify ứng viên = so 256 byte trong RAM (mean absolute error), **không phải
đọc lại file gốc từ đĩa**. Đây là thứ biến bước verify từ đắt thành gần như miễn phí.
Thumbnail này còn dùng lại được để render UI duyệt cụm trùng ở v0.5.

**Chống false positive bắt buộc:** ảnh gần đơn sắc (đen, trắng, scan trang trắng) cho
pHash giống hệt nhau hàng loạt. Tính độ lệch chuẩn của thumbnail, dưới ngưỡng thì tách
ra nhóm riêng, không gom cụm tự động.

---

## Thư viện decode — đây mới là quyết định thật sự

Toàn bộ chi phí nằm ở decode ảnh, không nằm ở hash. Nên chọn decoder mới là chọn quan trọng.

| Thư viện | RAW | HEIC | Scaled decode | Đánh giá |
|---|---|---|---|---|
| **WIC (Windows Imaging Component)** | ✅ qua Raw Image Extension | ⚠️ cần HEIF ext + codec HEVC | ✅ 1/2, 1/4, 1/8 | **Chọn cái này** |
| NetVips (libvips) | ❌ | ⚠️ | ✅ shrink-on-load | Nhanh nhất, nhưng thiếu RAW |
| Magick.NET | ✅ | ✅ | ✅ `jpeg:size` | Đủ format nhất, nặng ~100MB, chậm hơn |
| ImageSharp | ❌ | ❌ | ❌ | Thuần managed, nhưng **license thương mại** trên ngưỡng doanh thu |
| SkiaSharp | ❌ | ⚠️ | hạn chế | Không hợp |

**Chọn WIC**, vì:
- Đã có sẵn trong Windows, **không thêm dependency, không tăng dung lượng deploy**.
- Chính là decoder Explorer dùng → format nào Explorer xem được thì tool đọc được.
- Hỗ trợ **scaled decode** qua `IWICBitmapSourceTransform::GetClosestSize` → decode JPEG
  ở 1/8 kích thước, nhanh gấp ~8–16 lần. Đúng thứ cần cho pHash.
- Đọc được **thumbnail nhúng** qua `IWICBitmapFrameDecode::GetThumbnail` — fast path vài KB.
- RAW: cài "Raw Image Extension" của Microsoft từ Store, miễn phí.

**Cảnh báo phải biết trước:**
- HEIC qua WIC cần "HEIF Image Extensions" (miễn phí) **và** một bộ decode HEVC. Gói
  "HEVC Video Extensions" mất phí (~1 USD) trừ khi máy OEM có sẵn. Không đảm bảo có.
- RAW qua WIC không phủ hết mọi đời máy ảnh.

**Đường vòng cho RAW, nên làm bất kể chọn gì:** mọi file RAW đều **nhúng sẵn một ảnh JPEG
preview cỡ lớn** bên trong. Trích thẳng JPEG đó ra rồi decode như JPEG thường — không cần
demosaic, không cần bộ giải mã RAW nào cả, nhanh hơn nhiều lần. Đây là cách thực dụng nhất
để xử lý `.NEF` / `.CR2` / `.ARW` trong bài toán dò trùng.

Đổi lại, WIC là COM nên interop từ C# dài dòng. Đây là chi phí thật, nhưng chỉ tốn một lần.

---

## Tầng 4: AI — chưa làm, và nói rõ khi nào mới đáng làm

Chỉ cần khi muốn bắt **crop, watermark, chèn chữ, chỉnh màu nặng**. Không cần cho yêu cầu
"thiếu metadata" của bạn.

Nếu đến lúc cần:

| Thành phần | Chọn |
|---|---|
| Runtime | **ONNX Runtime** (`Microsoft.ML.OnnxRuntime`), + `.DirectML` để chạy GPU trên mọi card Windows kể cả AMD/Intel |
| Model | **CLIP ViT-B/32** image encoder (512 chiều) — hoặc DINOv2-S nếu ưu tiên near-duplicate hơn semantic |
| Index | HNSW |

**Nguyên tắc quan trọng:** chỉ chạy embedding trên **phần dư** — số ảnh mà tầng 1–3 đã
không gom được vào cụm nào. Chạy AI lên toàn bộ 2 triệu ảnh là lãng phí lớn, vì 90%+ số
bản trùng thực tế đã bị tầng 2 và 3 bắt hết rồi.

---

## Những thứ KHÔNG dùng, và lý do

- **SSIM / so histogram / so pixel trực tiếp** để ghép cặp — bắt buộc so đôi một, O(N²).
  Với 2 triệu ảnh là 2×10¹² phép. Bất khả thi. Chỉ dùng được ở bước *verify* trên tập ứng
  viên nhỏ.
- **aHash (average hash)** — yếu hơn dHash rõ rệt mà không rẻ hơn. Không có lý do dùng.
- **MD5 / SHA-1 / SHA-256** cho tầng 1 — chậm hơn nhiều lần, không được lợi gì. Bảo vệ
  chống va chạm cố ý là không liên quan trong bài toán này; an toàn khi xoá đến từ việc
  **so byte trực tiếp trước khi xoá**, không đến từ độ mạnh của hash.
- **ImageSharp** nếu có ý định thương mại hoá — license Six Labors Split đòi bản quyền
  trả phí trên ngưỡng doanh thu. Cân nhắc trước khi lỡ gắn sâu.
- **Gọi API AI trên cloud** — vài triệu ảnh, chi phí và thời gian đều vô lý, chưa kể phải
  upload toàn bộ thư viện ảnh cá nhân lên mạng.

---

## Thứ tự làm

Một lượt decode duy nhất sinh ra **cả bốn** thứ sau, không decode lại lần nào:

```
decode ảnh (scaled 1/8 nếu là JPEG)
   ├─→ pixel_hash   (xxHash128 buffer pixel đã chuẩn hoá)  ← tầng 2
   ├─→ dHash 64-bit                                        ← tầng 3
   ├─→ pHash 64-bit                                        ← tầng 3
   └─→ thumbnail 16×16 grayscale, 256 byte                 ← verify + UI
```

Đây là điểm thiết kế then chốt: decode là chi phí thống trị, nên phải vắt kiệt mỗi lần
decode. Bốn thứ trên đủ để dựng toàn bộ tầng 2 và 3, và đủ để verify mà không đụng lại đĩa.

---

## Phụ lục: kết quả nghiệm thu thật (2026-09-01)

Dựng một bộ ảnh có kiểm soát từ một file gốc 5.157 KB trên ổ F:, rồi chạy toàn bộ pipeline.

### Tầng 2 — pixel hash: đúng như thiết kế

| Biến thể | Dung lượng file | pixel hash |
|---|---|---|
| Gốc | 5.280.381 byte | `B0C0FEA697D25DA7F7F8B61B422D23F1` |
| **Thêm** khối metadata (COM) | 5.280.416 byte | `B0C0FEA697D25DA7F7F8B61B422D23F1` |
| **Xoá** metadata (cắt 61.722 byte APPn/COM) | 5.218.659 byte | `B0C0FEA697D25DA7F7F8B61B422D23F1` |

Ba file khác hẳn nhau về byte → hash file khác nhau hoàn toàn → tầng 1 **không** bắt được.
Pixel hash **khớp tuyệt đối**. Đây chính là ca "trùng nhưng thiếu metadata".

### Tầng 3 — pHash/dHash: bền hơn dự đoán

| Biến thể | dhash | phash | Hamming |
|---|---|---|---|
| Gốc | `3e1816cd4a72edf9` | `363434df783a4d71` | — |
| Resize 50% | `3e1816cd4a72edf9` | `363434df783a4d71` | **0** |
| Nén lại JPEG q55 | `3e1816cd4a72edf9` | `363434df783a4d71` | **0** |
| Nén nát JPEG q10 | `3e1816cd4a72edf9` | `363434df783a4d71` | **0** |
| Chỉnh sáng +16% | `3e1816cd4a63ede9` | `343d30df783a4d71` | **3** |
| **Crop 6% mỗi cạnh** | `3e1a564ddaf7ade1` | `3c3030cff8ba6d71` | **8** |
| Ảnh khác hoàn toàn | `a483e0e9e8f1d1d1` | `74822dc371b4f9e9` | 30+ |

Kết luận rút ra từ số liệu:
- Resize và nén lại **không làm đổi hash một bit nào**. Ngưỡng 4 là quá dư cho các ca này.
- Chỉnh sáng chỉ lệch 3 bit → vẫn trong ngưỡng.
- **Crop 6% lệch 8 bit → vượt ngưỡng 4, không bắt được.** Xác nhận đúng điều đã nói ở trên:
  crop là ca của tầng 4 (embedding), pHash không giải quyết được. Nới ngưỡng lên 8 để bắt
  crop sẽ kéo theo bùng nổ false positive — không phải cách đúng.

### Một lỗi thiết kế do test phát hiện

Bản đầu dùng MAE tuyệt đối để verify. Ảnh chỉnh sáng có Hamming = 3 (qua được hash) nhưng
**bị bước verify loại**, vì tăng sáng 16% đẩy mọi mức xám lên ~40 → MAE ≈ 40, vượt ngưỡng 8.

Nghịch lý: pHash cố tình bỏ hệ số DC để **bất biến với độ sáng**, rồi bước verify lại so
mức xám tuyệt đối — verify triệt tiêu đúng ưu điểm của hash.

Đã sửa: chuẩn hoá thumbnail thứ hai về đúng mean và độ lệch chuẩn của thumbnail thứ nhất
trước khi so. Sau khi sửa, ảnh chỉnh sáng được gom đúng, các ca khác không đổi.

### Nghiệm thu clustering

9 file (7 biến thể cùng ảnh + 1 bản crop + 1 ảnh khác):

```
[same photo, mixed versions] 7 files -> 12.7 MB reclaimable
   A  3 files, identical picture - differ in metadata only:   ← tầng 2
      2953x4430   5.0 MB   01-goc.jpg
      2953x4430   5.0 MB   02-them-metadata.jpg
      2953x4430   5.0 MB   03-xoa-metadata.jpg
   B  2953x4430   1.2 MB   08-sang-hon.jpg                    ← tầng 3
   C  2953x4430   629 KB   05-nen-lai-q55.jpg                 ← tầng 3
   D  1476x2215   577 KB   04-resize-50pc.jpg                 ← tầng 3
   E  2953x4430   287 KB   06-nen-nat-q10.jpg                 ← tầng 3
```

`07-crop-6pc.jpg` và `90-anh-khac.jpg` đều **không** bị gom — đúng như mong đợi.

### Thư viện decode: WIC hoạt động

Đọc được JPEG và **RAW `.NEF` gốc 6016×4016** trực tiếp, không cần thư viện ngoài
(máy đã có Raw Image Extension). Ba lỗi interop gặp phải, đều do IID/vtable, đã ghi lại
trong `Native/Wic.cs`: cách chắc chắn là chỉ QueryInterface về `IWICBitmapSource`
(IID `00000120-...`) thay vì `IWICBitmapFrameDecode`.

---

## Phụ lục 2: v0.4 và ba lỗi test bắt được

### Lỗi 1 — cộng dồn điểm để vị trí thư mục đè được metadata

Bản đầu chấm điểm bằng cách cộng: có EXIF +1000, có ngày chụp +800, nằm trong thư mục
ưu tiên +1500, tên đẹp +250, v.v. rồi so tổng.

Phép thử: đặt bản **mất metadata** vào thư mục ưu tiên với tên đẹp, đặt bản **còn đủ EXIF**
vào thư mục backup với tên `IMG_9999.jpg`. Kết quả: tool giữ bản **không có metadata**.

Đây đúng là ca mà chính sách sinh ra để ngăn. Với điểm cộng, đủ nhiều bằng chứng yếu sẽ
đè được một bằng chứng mạnh.

Đã sửa: bỏ cộng dồn, chuyển sang **so sánh theo thứ bậc** (`KeeperPolicy.Compare`). Tầng
dưới chỉ được phá thế hoà của tầng trên, không bao giờ lật ngược nó. Điểm số nay chỉ còn
đóng vai trò tie-break ở tầng 5 và chỉ chứa vị trí + tên file.

### Lỗi 2 — regex phạt oan chính những file cần bảo vệ

`CopySuffix` bắt cả mẫu `[-_ ]\d+$`, nên `IMG_9999.jpg` bị coi là "bản sao" và trừ điểm.
Nhưng đó là **số đếm của máy ảnh** — tức là dấu hiệu của file gốc, không phải bản sao.
Đã thu hẹp về đúng các dấu vết mà thao tác copy để lại: `(1)`, `- Copy`.

### Lỗi 3 — thứ tự khởi tạo static field giết cả bộ đọc EXIF

```csharp
private static readonly int[] StandardLuminanceZigzag = BuildZigzag([...]);  // dung ZigzagOrder
private static readonly int[] ZigzagOrder = [...];                            // khai bao SAU
```

Static field khởi tạo theo thứ tự khai báo, nên `BuildZigzag` đọc `ZigzagOrder` khi nó còn
`null` → type initializer chết → **mọi lần gọi `JpegMetadata.Read` về sau đều ném
`TypeInitializationException`**.

Và nó bị che hoàn toàn bởi:

```csharp
catch { return ImageMetadata.None; }
```

Nên toàn bộ thư viện hiện ra là "không file nào có metadata" — mà với chính sách chọn bản
giữ thì đó **đọc như một bằng chứng**, không phải như một lỗi.

Đã sửa hai chỗ:
- Khai báo `ZigzagOrder` **trước** bảng dùng nó.
- `catch` chỉ nuốt đúng loại lỗi I/O dự kiến được (`IOException`, `UnauthorizedAccessException`,
  `ArgumentException`, `NotSupportedException`). Lỗi lập trình phải nổi lên, không được
  cải trang thành dữ liệu.
- Thêm `MetadataVersion` trong `PlanBuilder`: catalog đã cache giá trị của bộ đọc hỏng sẽ
  tự động được đọc lại thay vì tin vào số 0 cũ.

Sau khi sửa, cùng file đó đọc ra: `48 tags, date=2025-02-05 07:53, camera=SONY ILCE-7M4,
quality~99, metadata=61.722 bytes`.

### Nghiệm thu an toàn

| Phép thử | Kết quả |
|---|---|
| `plan` trên bẫy metadata-vs-thư mục | giữ đúng bản có ngày chụp |
| `apply` không có `--execute` | không đụng file nào |
| `apply --execute` | chuyển 1 file, ghi manifest |
| `undo --batch` | trả lại nguyên vẹn |
| **plan bịa: ghép 2 ảnh khác hẳn nhau** | **`VERIFICATION FAIL - bytes differ`, không chuyển gì** |

Phép thử cuối là quan trọng nhất: kể cả khi file plan bị sửa sai (hoặc do người dùng sửa
nhầm), bước xác minh tại thời điểm thực thi vẫn chặn lại.

### Còn thiếu ở v0.4

**Merge EXIF chưa làm.** Khi bản giữ thiếu metadata mà bản bị loại có, hiện tool chỉ
**cảnh báo** (`ATTENTION: N groups where a copy being removed has more metadata than the
one kept`), chưa tự ghi EXIF sang. Việc ghi là splice lại segment APP1 giữa hai file JPEG —
không decode, không đổi pixel — nhưng là thao tác **ghi đè lên file gốc**, nên cần làm cẩn
thận hơn mọi thứ đã làm cho tới giờ.

### Lỗi 4 — đọc 4 MB mỗi file để lấy vài KB metadata

Bắt được ngay khi chạy `plan --exact` lần đầu trên catalog thật: 66.190 file cần đọc
metadata, mà `MaxScanBytes` đang đặt 4 MB → **~260 GB đọc từ HDD** cho dữ liệu nằm gọn
trong vài chục KB đầu mỗi file.

Mọi thứ bộ đọc cần — segment APP1 chứa EXIF, bảng lượng tử DQT, header khung SOF — đều
nằm **trước** dữ liệu nén. Một segment JPEG cũng không thể vượt 64 KB (trường length chỉ
16 bit). TIFF/RAW để IFD0 ngay sát header.

Đã hạ xuống **256 KB**. Kiểm lại: JPEG vẫn ra 48 tags, và `.NEF` vẫn ra 61 tags +
`NIKON D750` + GPS — không mất gì.

Kèm theo, sửa một lỗi tiềm ẩn cùng chỗ: `stream.Read` có thể trả về ít hơn số byte yêu
cầu, và buffer đọc thiếu sẽ cắt ngang một segment giữa chừng → trông y hệt như "file này
không có metadata". Đã thay bằng vòng lặp đọc cho đủ.

Bài học lặp lại từ lỗi 3: **trong bài toán này, "không có metadata" là một kết luận có
hậu quả** — nó quyết định bản nào bị loại. Nên mọi đường dẫn code có thể sinh ra kết luận
đó vì lý do kỹ thuật (exception bị nuốt, buffer đọc thiếu, cache cũ) đều phải bị chặn.

---

## Phụ lục 3: lỗi nghiêm trọng nhất — ảnh chụp liên tiếp bị coi là bản trùng

Phát hiện khi mở app trên catalog thật, không phải bằng test dựng sẵn. Cụm đứng đầu danh
sách là 14 file, 294 MB:

```
IMG20210501172347.jpg   17:23:47
IMG20210501172349.jpg   17:23:49
IMG20210501172351.jpg   17:23:51
IMG20210501172359.jpg   17:23:59
IMG20210501172403.jpg   17:24:03
IMG20210501172409.jpg   17:24:09
```

Cùng cảnh, cùng khung hình, cùng ánh sáng, chụp cách nhau vài giây — nhưng **tư thế người
trong ảnh khác nhau**. Đây là 14 tấm ảnh khác nhau của một buổi chụp, không phải 14 bản
sao của một tấm.

Tool đang đề xuất **bỏ 13/14 tấm**.

### Vì sao mọi tầng đều không chặn được

- pHash/dHash: khoảng cách 0 bit. Đúng — về mặt thị giác chúng gần như y hệt.
- Thumbnail verify: MAE rất thấp. Đúng — 16×16 xám không đủ phân giải để thấy khác biệt.
- Guard low-contrast: không liên quan.

Đây là **giới hạn bản chất của hash tri giác**, không phải lỗi ngưỡng. Nới chặt ngưỡng sẽ
làm mất khả năng bắt ảnh nén lại — mà vẫn không phân biệt được hai khung hình liền nhau.

### Lời giải: dùng bằng chứng phi thị giác

Thứ phân biệt chúng không nằm trong pixel. Nó nằm ở chỗ **máy ảnh đã ghi lại hai thời
điểm khác nhau**.

> Bản trùng thật sinh ra từ **copy, nén lại, hoặc resize**. Không thao tác nào trong số đó
> **bịa ra một giờ chụp mới**. Nên nếu hai file đều có `DateTimeOriginal` mà giá trị khác
> nhau, chúng là hai bức ảnh, không phải hai bản của một bức.

Cài trong `SimilarityIndex.Evaluate`, chạy **trước** cả phép so Hamming:

```csharp
if (data.DateTaken[i] != 0 && data.DateTaken[j] != 0 && data.DateTaken[i] != data.DateTaken[j])
    return;   // hai thoi diem khac nhau -> hai buc anh
```

### Đo trên dữ liệu thật (37.887 ảnh, đã loại `_mvc`)

| | Trước guard | Sau guard |
|---|---|---|
| Cụm cần người duyệt | 924 | **619** |
| File dư thừa | 18.095 | 17.571 |
| Đề xuất thu hồi | 33,0 GB | 30,1 GB |
| **Cặp bị từ chối vì khác giờ chụp** | — | **593.318** |
| Ca "cùng ảnh, mất metadata" (tầng 2) | 16.300 | **16.300 — không đổi** |

Dòng cuối là điều quan trọng nhất: guard **không hề đụng tới tầng 2**. Bản bị strip
metadata có `pixel_hash` giống hệt bản gốc nên được gộp trước, không đi qua đường tầng 3.
Nghĩa là guard cắt đúng false positive mà không mất một ca thật nào.

### Cái giá phải trả

Guard cần giờ chụp, mà **5.070/37.887 ảnh không có EXIF**. Với những ảnh đó tool vẫn phải
phán đoán bằng thị giác, và vẫn có thể gom nhầm ảnh chụp liên tiếp. `similar` nay in ra
con số này thay vì im lặng.

Đã thêm lệnh `mediatool metadata` để đọc EXIF cho toàn catalog **trước** khi gom cụm —
trước đây metadata chỉ được đọc trong `plan`, tức là *sau* khi cụm đã hình thành, quá muộn
để dùng làm guard.

### Bài học

Ba lỗi nặng nhất của dự án này (§Lỗi 3, §Lỗi 4, và cái ở đây) đều cùng một dạng: **một kết
luận có hậu quả được rút ra từ chỗ thiếu thông tin, chứ không phải từ bằng chứng.**
"Không có metadata" vì bộ đọc hỏng. "Không có metadata" vì buffer đọc thiếu. "Là bản
trùng" vì chỉ nhìn pixel mà không có giờ chụp để đối chiếu.

Trong bài toán xoá file, thiếu dữ liệu phải được xử lý như *thiếu dữ liệu*, không được im
lặng biến thành một kết luận.

---

## Phụ lục 4: soi từng cụm trên app — bốn lỗi nữa

Sau khi thêm guard giờ chụp, mở app duyệt từng cụm. Mỗi cụm nhìn kỹ lại lộ ra một lỗi mới.

### Lỗi 5 — giữ JPEG, vứt RAW

Cụm `NTN_5200`: 4 file gồm `.JPG` (17,4 MB) và `.NEF` (28,5 MB), mỗi loại 2 bản ở 2 thư mục.
Tool giữ **JPEG** và đề xuất bỏ **NEF**.

RAW là âm bản; JPEG là bản in ra từ nó. Vứt âm bản để giữ bản in là sai lầm duy nhất ở đây
**không thể hoàn tác**.

Nhưng cách sửa không phải "cho RAW điểm cao hơn". Vấn đề sâu hơn: **RAW và JPEG không phải
bản trùng của nhau.** Bản trùng thật là hai bản `.NEF` ở hai thư mục, và hai bản `.JPG` ở
hai thư mục. Nên `PlanBuilder` giờ **tách nhóm theo hạng định dạng trước** khi chọn bản giữ:
một thư mục chứa NEF+JPG nhân đôi cho ra **hai quyết định** — bỏ bản NEF thừa, bỏ bản JPG
thừa — thay vì một quyết định vứt mất âm bản.

Kèm theo `FormatTier`: RAW > lossless (PNG/TIFF) > lossy (JPEG/HEIC), đặt trên cả độ đầy đủ
EXIF trong chuỗi so sánh.

### Lỗi 6 — guard mù trong phạm vi một giây

Cụm `NTN_5177` + `NTN_5178`: hai khung hình liên tiếp, **cùng ghi 08:09**. Guard không
chặn được vì `DateTimeOriginal` chỉ phân giải tới **giây**, mà Nikon D750 chụp 6,5 hình/giây.

Số liệu thật:

```
NTN_5177.JPG   date_taken=1744531757   sub_sec=27
NTN_5178.JPG   date_taken=1744531757   sub_sec=81   ← cùng giây, cách 0,54s
```

Đã đọc thêm EXIF `SubSecTimeOriginal` (tag 0x9291) và đưa vào guard.

### Lỗi 7 — ảnh xuất từ editor mất hẳn giờ chụp

Cụm `_MG_8676` → `_MG_8686`, đường dẫn `...\Untitled Export`. Tất cả đều "EXIF, no date" —
Lightroom xuất ra không ghi `DateTimeOriginal`. Guang giờ chụp bất lực.

Thêm bằng chứng thứ ba, cũng phi thị giác: **số thứ tự khung hình trong tên file**. Máy ảnh
đánh số tuần tự và không bao giờ dùng lại một số; sao chép file thì không đổi số. Bản sao
của `_MG_8676.jpg` tên là `_MG_8676 (1).jpg` hoặc nằm ở thư mục khác cùng tên — **không bao
giờ** thành `_MG_8677.jpg`.

Nên: cùng tiền tố, khác số đuôi → hai lần bấm máy.

### Lỗi 8 — guard bị đi vòng, rồi bị bắc cầu

Hai chỗ hở, đều nghiêm trọng vì chúng làm guard *trông như* đang chạy mà thật ra không:

**(a) `CollapseIdentical` gộp trước khi guard chạy.** Bước này gom các file có hash
**giống hệt** vào một đại diện để MIH khỏi làm việc thừa. Nhưng hai khung hình liên tiếp
thường xuyên cho ra **đúng cùng một hash 64-bit** — nên chúng bị gộp ở đây và không bao giờ
đi qua `Evaluate`. Đã cho bước này chạy cùng bộ guard, tách một tập hash-giống-nhau thành
nhiều nhóm theo từng lần bấm máy.

**(b) Union-find có tính bắc cầu.** Guard chỉ có thể **từ chối một cạnh**; nó không ngăn
được một cụm hình thành quanh cạnh khác. Một bản copy **không có giờ chụp** nối được cả
`144445` lẫn `144446` — guard không có cơ sở để từ chối nó với bên nào — và thế là hai lần
bấm máy nằm chung một cụm.

Đã thêm bước **tách lại sau khi gom**: cụm nào chứa nhiều hơn một lần bấm máy thì bị chia
theo giờ chụp. File không có giờ chụp đi theo nhóm nào đang giữ **đúng pixel hash của nó** —
đó chính là bản bị strip metadata, nó thuộc về bản gốc đã sinh ra nó — còn không thì đứng
riêng, không được phép bắc cầu lần nữa.

### Kết quả từng bước (37.887 ảnh, đã loại `_mvc`)

| Bước | Cụm cần duyệt | Đề xuất thu hồi |
|---|---|---|
| Ban đầu (toàn catalog) | 4.130 | 40,1 GB |
| Lọc phạm vi `--exclude _mvc` | 924 | 33,0 GB |
| Guard giờ chụp (giây) | 619 | 30,1 GB |
| Tách RAW/JPEG + sub-second | 363 | 28,5 GB |
| Guard số thứ tự tên file | 312 | 28,4 GB |
| Guard trong `CollapseIdentical` | 277 | 28,2 GB |
| Tách lại sau khi gom cụm | **272** | **28,2 GB** |

Xuyên suốt, con số **"cùng ảnh, mất metadata" giữ nguyên ~16.300** — toàn bộ việc siết chặt
này cắt đúng false positive mà **không mất một ca thật nào** của yêu cầu ban đầu.

Số cụm phải duyệt tay giảm **15 lần**.

### Điểm chung của cả tám lỗi

Không lỗi nào là lỗi thuật toán hash. Tất cả đều là: **một kết luận có hậu quả được rút ra
ở chỗ thiếu bằng chứng** — thiếu vì bộ đọc hỏng, vì buffer đọc thiếu, vì chưa đọc metadata,
vì editor đã xoá nó, hoặc vì guard bị đi vòng.

Trong bài toán xoá ảnh của người khác, chỗ thiếu dữ liệu phải được xử lý như **chỗ thiếu dữ
liệu**, không được lặng lẽ trở thành một câu trả lời.

---

## Phụ lục 5: kiểm soát việc xoá, và test tự động

### Trước hết: tool không có khả năng xoá ảnh

Quét toàn bộ source tìm mọi lệnh có thể phá dữ liệu — `File.Delete`, `Directory.Delete`,
`File.WriteAllBytes`, `File.Copy`, `FileMode.Create`, `FileAccess.Write`, `SetLength` — kết
quả: **không có cái nào** chạm vào ảnh người dùng. Chỉ có `File.Move`.

Điều đó có nghĩa: kể cả toàn bộ logic dò trùng sai hết, hậu quả xấu nhất là file **nằm nhầm
chỗ** trong quarantine, và `undo` lấy lại được.

Việc này nay là một **test**, không phải một lời hứa: `SafetyInvariantTests` đọc chính mã
nguồn và bắt buộc số lượng `File.Delete` = 1 (trong purger) và `File.Move` = 3 (vào
quarantine, undo qua catalog, undo qua manifest). Thêm bất kỳ đường nào chạm vào file là
test đỏ ngay, trước khi ai đó phát hiện ra trên thư viện thật.

### Vòng đời có thời gian chờ

Trước đây quarantine là ngõ cụt: file nằm đó mãi, đĩa không bao giờ thật sự trống. Nay có
vòng đời đầy đủ:

```
apply --execute   → chuyển vào quarantine
history           → còn bao lâu nữa mới được purge
undo              → trả về, bất cứ lúc nào trước khi purge
purge --execute   → xoá hẳn
```

`purge` là **lệnh duy nhất không thể hoàn tác** trong toàn bộ chương trình, nên nó có năm
lớp chặn, mỗi lớp cho một kiểu sai khác nhau:

| Chặn | Chống được gì |
|---|---|
| Thời gian chờ mặc định **14 ngày** | Quyết định sai có nhiều ngày để nhận ra, không phải vài giây |
| Chỉ xoá file có trong bảng `actions` với `state='done'` | Không đụng file lạ |
| **Kiểm tra đường dẫn nằm trong thư mục quarantine** | Bản ghi hỏng hoặc bị sửa tay không lái được nó về thư viện gốc |
| Dung lượng phải khớp với lúc chuyển vào | Không xoá thứ đã bị thay thế sau đó |
| Mặc định chạy thử; `--execute` rồi vẫn phải **gõ đúng tên batch** | Gõ nhầm cờ không đủ để kích hoạt |

Kiểm chứng lớp thứ ba bằng cách phá hoại có chủ đích: sửa thẳng SQLite cho một bản ghi purge
trỏ vào ảnh gốc. Kết quả: `REFUSED — not inside the quarantine folder`, ảnh gốc nguyên vẹn.

### Undo không phụ thuộc vào catalog

Mỗi batch có `manifest.csv` viết ngay trong thư mục quarantine, đủ thông tin để phục hồi mà
không cần database. Mất catalog, đổi máy, hay chỉ là copy thư mục quarantine sang chỗ khác —
`undo --manifest` vẫn chạy. Catalog là tiện lợi; manifest mới là bảo đảm.

### 80 test, phân bổ theo mức thiệt hại

Không rải đều. Phần lớn nằm ở đường có thể mất ảnh:

| Nhóm | Số test |
|---|---|
| Bất biến an toàn (quét source) | 6 |
| Vòng đời quarantine trên file thật | 16 |
| Guard chống nhầm ảnh chụp liên tiếp + hash | 14 |
| Chính sách chọn bản giữ | 10 |
| Bộ đọc EXIF | 8 |

Mỗi test tương ứng một lỗi **đã thật sự xảy ra** trong quá trình làm, không phải test cho đủ
số. Ví dụ `AHumanReviewedDecisionCanActuallyBeExecuted` tồn tại vì nút Apply của app từng
**không chuyển được file nào** suốt cả quá trình phát triển mà không ai biết.

### Hai lỗi mà chính bộ test vừa tìm ra

Viết test xong chạy lần đầu đã ra hai lỗi:

**Bộ đọc EXIF ghép tên máy ảnh thừa.** `Make="NIKON CORPORATION"` + `Model="NIKON D750"` cho
ra `"NIKON CORPORATION NIKON D750"`. Đã sửa: nếu Model bắt đầu bằng từ đầu tiên của Make thì
chỉ lấy Model.

**Test đếm `File.Move` bắt được đúng thứ nó sinh ra để bắt.** Con số nhảy từ 2 lên 3 vì tôi
vừa thêm `UndoFromManifest`. Đây là hành vi mong muốn: mọi đường mới chạm vào file của người
dùng đều phải được khai báo có ý thức, không được lặng lẽ xuất hiện.

---

## Phụ lục 6: merge EXIF — biến việc xoá thành việc làm giàu

Phần còn nợ từ v0.4, nay đã làm.

### Vấn đề nó giải quyết

Chính sách chọn bản giữ ưu tiên ngày chụp rất cao, nên phần lớn trường hợp nó **tự tránh**
được việc mất metadata. Nhưng có hai lý do mạnh hơn metadata trong chuỗi so sánh: **độ phân
giải** và **hạng định dạng**. Khi bản to hơn lại là bản đã bị editor xoá sạch EXIF — kiểu
`Untitled Export` đã thấy trong thư viện thật — thì giữ nó đồng nghĩa với mất vĩnh viễn
thông tin duy nhất còn lại về thời điểm chụp.

`plan` vốn đã cảnh báo ca này (`ATTENTION: N groups where a copy being removed has more
metadata than the one kept`). Giờ nó xử lý được.

### Cắt-ghép ở mức byte, không decode

JPEG là chuỗi các khối: metadata, rồi dữ liệu ảnh nén, rồi marker kết thúc. Hai phần **không
chồng lên nhau**, nên thay khối metadata chỉ là copy nguyên xi phần hai bên và đặt khối khác
vào giữa. Không decode, không encode, nên **không có mất mát thế hệ**.

Đó chính là lý do đáng làm theo cách này. Nếu phục hồi ngày chụp bằng cách mở ảnh ra rồi lưu
lại qua một thư viện xử lý ảnh, sẽ mất thêm một vòng nén lossy — trong khi mục đích của cả
việc này là **không mất gì**.

Đo trên ảnh thật (bản export 4134×6202 đã mất metadata, nhận EXIF từ bản gốc 2953×4430):

```
truoc:  pixel A057F9BFE34E425FE7B5233096A0B2A7   exif none
sau:    pixel A057F9BFE34E425FE7B5233096A0B2A7   exif 48 tags, 2025-02-05 07:53, SONY ILCE-7M4
```

### Đường ghi duy nhất, và cách nó được kiểm soát

Đây là chỗ **duy nhất** trong chương trình ghi vào thư viện của người dùng, nên nó theo đúng
nguyên tắc của phần xoá: **không bao giờ ghi đè**.

```
1. Ghi file moi duoi ten tam           (tao moi, khong the de len cai gi)
2. Tu choi neu ten tam da ton tai      (khong dam len viec dang do cua ai khac)
3. Xac minh: pixel hash phai Y HET ban goc, va ngay chup phai co mat
4. Chuyen ban goc vao quarantine       (ghi vao bang actions truoc)
5. Chuyen file moi vao cho cu          (duong dan luc nay da trong)
```

Bước 3 là bước quan trọng nhất: so pixel hash chứng minh thao tác cắt-ghép **không làm hỏng
dữ liệu ảnh và không vô tình nén lại**. Nếu ảnh khác đi dù chỉ một chút, file tạm bị xoá và
bản gốc không bị đụng tới.

Không có bước nào ghi đè lên file có sẵn. Crash giữa bước 4 và 5 để lại bản gốc trong
quarantine và đường dẫn cũ trống — `undo` phục hồi được.

### Bộ test an toàn đã làm đúng việc của nó

Thêm merge làm **ba test an toàn đỏ ngay**, vì nó đưa vào những khả năng chưa từng có:
`File.WriteAllBytes` (1 chỗ), `File.Delete` (thêm 2 chỗ), `File.Move` (thêm 2 chỗ).

Cách sửa **không phải** nới lỏng test, mà chuyển nó thành **danh sách khai báo có kiểm đếm**:

```csharp
private static readonly Dictionary<string, int> AllowedDeletes = new()
{
    ["QuarantinePurger.cs"] = 1,   // chinh viec purge
    ["MetadataMerger.cs"]   = 2,   // xoa file tam cua chinh no khi bi tu choi
};
```

Test bắt buộc **số lượng khớp chính xác** ở từng file. Nghĩa là không ai có thể thêm một
đường chạm vào file của người dùng mà không phải viết ra nó là gì và vì sao an toàn. Bản
thân việc thêm merge đã phải đi qua đúng cổng đó.

### Một lỗi nữa lộ ra khi kiểm chứng

Chạy `probe` với đường dẫn dùng dấu `/` thì fail `0x80070002 (file not found)`. Nguyên nhân:
tiền tố `\?\` **tắt chuẩn hoá đường dẫn**, kể cả việc chấp nhận dấu gạch xuôi. Đường dẫn
lấy từ catalog luôn dùng `\` nên không bao giờ gặp, nhưng đường dẫn người dùng gõ vào thì có.
Đã chuẩn hoá trong `LongPath.Prefix` và thêm test.

---

## Phụ lục 7: hardlink — thu hồi dung lượng mà không xoá gì

Dành cho tình huống "sợ mất data": với ảnh trùng byte trên cùng một ổ NTFS, biến bản trùng
thành **hardlink** trỏ vào bản gốc. Cả hai đường dẫn vẫn mở được, vẫn hiện trong thư mục,
vẫn hoạt động với mọi phần mềm — nhưng đĩa chỉ lưu nội dung một lần.

### Cửa sổ nguy hiểm, và cách bịt nó

Tạo link tại một đường dẫn đang có file **bắt buộc phải xoá file đó trước**. Nếu process
chết đúng khoảnh khắc đó, file mất hẳn.

Nên dùng đúng mẫu như merge và quarantine:

```
1. So byte lai lan nua (khong tin plan)
2. Chuyen ban trung vao quarantine + ghi vao bang actions
3. CreateHardLink tai duong dan vua trong
4. Xac minh: hai duong dan phai co CUNG file id
5. Neu buoc 3 hoac 4 that bai -> chuyen ban goc tro lai ngay
```

Bước 4 là bằng chứng, không phải giả định: `FILE_ID_INFO` trả về `(volume serial, file id
128-bit)`. Hai đường dẫn cùng giá trị nghĩa là chúng **thật sự là một file**. Nếu không
khớp, link bị xoá và bản gốc được đặt lại chỗ cũ.

Kiểm chứng độc lập bằng `fsutil hardlink list` — nó liệt kê mọi đường dẫn cùng trỏ vào một
file vật lý.

### Giới hạn cứng, được kiểm tra chứ không giả định

| Điều kiện | Vì sao |
|---|---|
| Chỉ `GroupKind.ExactBytes` | Gộp hai ảnh khác nhau thành một file là **phá huỷ một trong hai** |
| Cùng volume GUID | Hardlink không vượt được ranh giới ổ đĩa |
| NTFS hoặc ReFS | Các filesystem khác không hỗ trợ |
| Filesystem phải trả về file id | Không có id thì không xác minh được link |
| Chưa phải cùng một file rồi | Chạy lại phải là no-op |

### Đánh đổi phải nói rõ với người dùng

- **Sửa tại chỗ một đường dẫn sẽ đổi cả hai** — chúng là một file. Phần lớn phần mềm ảnh ghi
  ra file mới, nhưng không phải tất cả.
- Timestamp riêng của bản trùng bị thay bằng của bản gốc.

Vì vậy đây là **lựa chọn thay thế**, không phải mặc định. CLI in cả hai cảnh báo này ra
trước khi làm gì.

### Một lỗi nữa do chính test tìm ra

Bản đầu, `Undo` xác định "file đang chiếm chỗ có phải link của mình không" bằng cách **tra
bảng `files`** để lấy đường dẫn của bản giữ. Test thất bại vì môi trường test không có dòng
nào trong bảng đó — nhưng đó không phải lỗi của test, mà là **một phụ thuộc thừa**: undo
không nên cần catalog để biết một điều nó có thể tự kiểm chứng.

Thay bằng phép kiểm tự chứa: **so byte giữa file đang chiếm chỗ và bản gốc sắp phục hồi.**
Nếu giống nhau thì xoá nó không mất gì (link của ta thoả mãn điều này theo định nghĩa); nếu
khác thì đó là việc của người khác và được để nguyên. Ngắn hơn, đúng hơn, và không phụ thuộc
vào database.

### So sánh với quarantine

| | quarantine + purge | hardlink + purge |
|---|---|---|
| Dung lượng thu hồi | như nhau | như nhau |
| Sau khi purge | một đường dẫn **biến mất** | **cả hai vẫn mở được** |
| Rủi ro chính | chọn sai bản giữ → mất đường dẫn | sửa tại chỗ ảnh hưởng cả hai |
| Phạm vi áp dụng | mọi ổ, mọi tầng | cùng ổ, NTFS, chỉ trùng byte |
