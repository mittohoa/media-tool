# Thiết kế: Dò trùng ảnh quy mô lớn trên Windows

Trạng thái: **v0.1 đã chạy được** (crawl + catalog, đã đo trên 908k file thật — xem §9,
§11). Các tầng hash và pHash vẫn đang ở dạng thiết kế. Mục tiêu: xử lý hàng triệu file / nhiều TB,
rải rác trên nhiều thư mục – nhiều ổ đĩa – nhiều đĩa vật lý (kể cả đĩa tháo rời),
và bắt được cả bản trùng đã bị mất/xoá metadata (EXIF).

---

## 1. Nguyên tắc cốt lõi

**Cascade lọc từ rẻ đến đắt.** Không bao giờ hash toàn bộ dữ liệu. Mỗi tầng chỉ nhận
đầu vào là phần sống sót của tầng trước:

```
Quét MFT  →  loại hardlink  →  gom theo size  →  hash đầu/cuối  →  hash full
                                                                      ↓ (trùng byte)
                          decode thu nhỏ  →  pHash/dHash  →  MIH + union-find
                                                                      ↓ (trùng thị giác)
                                                      chấm điểm bản giữ  →  hành động
```

**Hai loại "trùng" khác nhau về bản chất, phải tách bạch:**

| Loại | Phát hiện bằng | Bắt được |
|---|---|---|
| Trùng byte (exact) | BLAKE3/xxHash3 full file | copy y hệt |
| Trùng thị giác (near) | pHash/dHash trên ảnh đã decode | resize, nén lại, **đã xoá EXIF**, đổi định dạng |

Yêu cầu "trùng nhưng thiếu metadata" nằm hoàn toàn ở nhánh 2: hai file cùng ảnh nhưng
một bản bị strip EXIF sẽ **khác byte hash** → chỉ pHash mới gom được. Đây là lý do
không thể làm tool chỉ dựa trên hash file.

---

## 2. Tầng quét (crawl)

- **Enumerate bằng `GetFileInformationByHandleEx(FileIdExtdDirectoryInfo)`** — đây là cái
  v0.1 dùng. Một buffer trả về đủ attributes + size + timestamps + reparse tag + FileId
  128-bit, không cần mở từng file, không cần quyền admin. Fallback `FILE_FULL_DIR_INFO`
  cho exFAT/FAT32. Chi tiết lý do chọn cái này thay vì MFT: §11.
- **MFT / USN Journal** (`FSCTL_ENUM_USN_DATA` trên `\\.\X:`) nhanh hơn nữa nhưng đòi
  admin và chỉ NTFS → để v0.2+, không phải điều kiện tiên quyết.
- **Định danh ổ đĩa bằng Volume GUID** (`\\?\Volume{GUID}\`), **không dùng chữ cái ổ**.
  Rút ra cắm lại là đổi letter → toàn bộ catalog hỏng. Đây là điều kiện bắt buộc cho
  bài toán "nhiều đĩa cứng".
- **Định danh file bằng `FILE_ID_INFO`** (VolumeSerial + FileId 128-bit) thay vì path.
  Đổi tên / di chuyển trong cùng volume vẫn nhận ra là file cũ → rescan gần như miễn phí.
- Bật long path (`\\?\` prefix hoặc manifest `longPathAware`).
- **Bỏ qua reparse point** (junction/symlink) → tránh lặp vô hạn và đếm trùng.
- **BẮT BUỘC bỏ qua file cloud placeholder**: `FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS`
  (0x00400000) và `FILE_ATTRIBUTE_OFFLINE`. Đọc phải nó là OneDrive/Dropbox tải về hàng TB.
  Đây là cái bẫy làm hỏng nhiều tool dedupe.

Bản ghi lưu: volume_guid, file_id, path, size, mtime, ext, attributes.

---

## 3. Tầng lọc trước khi hash

1. **Cùng `(volume_serial, file_id)` → cùng một file vật lý** (hardlink). Loại, không
   phải trùng lặp.
2. **Gom theo size.** Trùng byte bắt buộc cùng size. Nhóm size chỉ có 1 phần tử →
   loại khỏi nhánh exact (nhưng vẫn phải đi nhánh pHash).
3. **Hash 64KB đầu + 64KB cuối.** Giết phần lớn nhóm giả với I/O tối thiểu.
4. **Hash full**: BLAKE3 (song song, ~GB/s) hoặc xxHash3. **Không dùng MD5/SHA1** —
   chậm hơn nhiều lần mà không được lợi gì ở bài toán này.

Thực tế: chỉ khoảng 5–15% tổng dung lượng thực sự cần đọc hết.

---

## 4. Tầng thị giác (near-duplicate)

Đây là tầng đắt nhất vì **phải decode mọi ảnh**, không lọc trước được.

**Tối ưu decode:**
- JPEG: dùng DCT scaled decode `scale_num/scale_denom = 1/8` (libjpeg-turbo). Nhanh
  gấp ~8–16 lần full decode, ra ảnh đủ dùng cho hash 8×8/32×32.
- Nếu có EXIF thumbnail nhúng → đọc nó (chỉ vài KB đầu file). Fast path, nhưng ảnh
  "thiếu metadata" thường đã mất thumbnail → luôn cần fallback.
- RAW (CR2/NEF/ARW): dùng JPEG preview nhúng, không demosaic.
- HEIC/AVIF/WebP: cần decoder riêng, chi phí cao hơn JPEG.

**Chuẩn hoá trước khi hash** (bỏ qua sẽ ra hàng loạt false negative):
- Áp EXIF Orientation rồi mới hash.
- Chuyển grayscale.
- Resize về kích thước cố định bằng cùng một thuật toán (box/area).

**Hash:**
- dHash 64-bit (nhạy với resize/nén lại) + pHash 64-bit (DCT, bền hơn).
- Lưu thêm: aspect ratio, kích thước gốc, entropy của ảnh.
- Muốn bền với xoay 90/180/270: hash cả 4 hướng, giữ giá trị nhỏ nhất.
- Crop / watermark / chèn chữ: pHash **thua**. Nếu cần → tầng tuỳ chọn dùng embedding
  (CLIP/MobileNet) + ANN index. Đắt, để sau, không đưa vào v1.

**Ghép cặp — điểm quyết định hiệu năng:** so tất cả với tất cả là O(N²), 2 triệu ảnh
là 2×10¹² phép so → không khả thi. Dùng **Multi-Index Hashing**:

> Chia hash 64-bit thành `k = threshold + 1` band. Hai hash có Hamming distance ≤ threshold
> thì theo nguyên lý chuồng bồ câu **bắt buộc trùng khít ít nhất 1 band**.

Với threshold = 3 → 4 band × 16 bit. Chỉ cần 4 bảng băm / 4 index SQLite, tra cứu
exact-match, rồi verify Hamming trên tập ứng viên nhỏ. Sau đó **union-find** để gom cụm.

**Chống false positive:** ảnh gần như đơn sắc (đen, trắng, scan trang trắng) cho pHash
giống hệt nhau hàng loạt. Phải tính entropy/độ lệch chuẩn histogram, dưới ngưỡng thì
tách ra nhóm "low-entropy" riêng, không gom cụm tự động.

**Ảnh thu nhỏ vs ảnh gốc:** thumbnail 160px và bản 6000px có pHash giống nhau. Đừng
coi là duplicate ngang hàng — gán quan hệ "derived/variant" riêng dựa trên tỉ lệ
kích thước, để người dùng quyết.

---

## 5. Chiến lược I/O — nút thắt thật sự

CPU hầu như không bao giờ là bottleneck; đĩa mới là.

- **Phân biệt HDD/SSD**: `IOCTL_STORAGE_QUERY_PROPERTY` +
  `StorageDeviceSeekPenaltyProperty` → `IncursSeekPenalty`.
- **HDD: 1–2 luồng đọc cho mỗi đĩa vật lý.** Nhiều luồng trên HDD làm throughput sụp
  vì seek. SSD/NVMe: mở 8–16 luồng thoải mái.
- **Sắp xếp thứ tự đọc theo vị trí vật lý.** Chuẩn nhất là `FSCTL_GET_RETRIEVAL_POINTERS`
  lấy LCN rồi sort. Xấp xỉ rẻ hơn: sort theo (volume, thư mục cha, file_id).
- Tách pool I/O và pool CPU (decode/hash) qua bounded queue producer/consumer.
- Mở file với `FILE_FLAG_SEQUENTIAL_SCAN`; với hash full file lớn cân nhắc
  `FILE_FLAG_NO_BUFFERING` để không làm ô nhiễm cache OS.
- **Windows Defender real-time scan sẽ giết hiệu năng** khi mở hàng triệu file. Cần
  hướng dẫn người dùng thêm exclusion cho tool, hoặc chấp nhận chậm nhiều lần.

---

## 6. Lưu trữ & khả năng nối lại

SQLite (WAL mode), một file DB duy nhất. Đo thật: **~490 byte/file** (đường dẫn dài của ổ
dev; thư viện ảnh sẽ nhỏ hơn) → 2 triệu file ≈ 1 GB catalog.

Bảng v0.1: `meta`, `volumes`, `scans`, `crawl_frontier`, `files`.
Bảng sẽ thêm: `content_hashes`, `image_hashes`, `phash_bands`, `clusters`, `actions_log`.

- **Resumable**: crawl và hash đều có checkpoint. Job chạy nhiều giờ trên TB thì việc
  bị ngắt là chuyện chắc chắn xảy ra, không phải rủi ro.
- **Incremental**: rescan bỏ qua file có `(file_id, size, mtime)` không đổi.
- **Catalog theo volume, hoạt động offline**: ổ rút ra vẫn còn nguyên hash trong DB →
  vẫn so trùng được với ổ chưa cắm. Nhưng **không được xoá** một file chỉ vì "bản copy
  nằm trên ổ đang offline" trừ khi user bật cờ cho phép.

---

## 7. Chọn bản giữ lại (chỗ đa số tool làm sai)

Không bao giờ tự động xoá. Chấm điểm ưu tiên:

1. **Còn EXIF/XMP đầy đủ** — trực tiếp giải quyết yêu cầu "thiếu metadata": bản có
   metadata là bản đáng giữ.
2. Độ phân giải lớn hơn.
3. Ít bị nén lại hơn (ước lượng quality từ bảng lượng tử JPEG).
4. Ngày tạo cũ hơn.
5. Nằm trong thư mục được đánh dấu "curated" / ổ chính.
6. Tên file có nghĩa (`2019-hoi-an.jpg`) hơn tên rác (`download (3).jpg`, `IMG_1234 - Copy.jpg`).

**Tính năng nên có, khác biệt thật sự:** khi bản giữ lại thiếu EXIF mà bản trùng có →
đề xuất **merge metadata** (ghi EXIF/XMP từ bản kia sang bản giữ) trước khi bỏ bản trùng.

**Mức hành động, tăng dần:**
`report only` → `move to quarantine` (giữ nguyên cấu trúc thư mục tương đối + manifest
để undo) → `thay bằng hardlink` (chỉ cùng volume) → `delete`. Mặc định phải là report.

---

## 8. Đề xuất tech stack

| Phương án | Đánh giá |
|---|---|
| **Rust core + Flutter desktop UI (flutter_rust_bridge)** | **Khuyến nghị.** jwalk / blake3 / zune-jpeg / rusqlite / rayon đều rất mạnh. Tận dụng được kinh nghiệm Flutter sẵn có của workspace. |
| C# / .NET 8 | Truy cập Win32 (USN, FileId, IOCTL) thuận nhất, có sẵn `System.IO.Hashing.XxHash128`, Magick.NET. Tốt nếu ưu tiên tốc độ phát triển. |
| Dart thuần | **Không nên** cho phần core. Decode ảnh trong Dart quá chậm, isolate không cứu được. |

---

## 9. Số đo thật (đo trên máy này, 2026-09-01)

Quét toàn bộ ổ **D: (HDD, NTFS, 325 GB)** bằng `mediatool scan D:\ --all --min-size 0`:

| Chỉ số | Giá trị |
|---|---|
| File vào catalog | 907.948 |
| Dung lượng | 261,5 GB |
| Thư mục | 362.895 |
| Thời gian (cache lạnh, HDD) | **8 phút 11 giây** |
| Tốc độ | ~1.750 file/giây |
| Kích thước catalog | 424 MB (~490 byte/file) |
| Độ phủ FileId | 100% (NTFS) |
| Quét lại cache nóng | 12.600 file/giây |

Hiệu quả của cascade trên chính tập này:

| Giai đoạn | Dung lượng phải đọc |
|---|---|
| Tổng dữ liệu | 261,5 GB |
| Sau lọc theo size (stage 1) | 139,5 GB ứng viên |
| **Stage 2 thực sự đọc** (64KB đầu+cuối, không quá size file) | **8,7 GB — 3,3% tổng** |

> Lưu ý về mẫu đo: ổ D: là ổ dev đầy build artifact, nên lọc theo size **kém hiệu quả
> bất thường** (874k/908k file rơi vào nhóm trùng size do vô số file nhỏ trùng dung lượng
> ngẫu nhiên). Trên thư viện ảnh thật, stage 1 cắt mạnh hơn nhiều. Con số 3,3% ở trên vì
> vậy là **cận trên bi quan**, không phải best case.

Ngoại suy cho vài TB ảnh (~2 triệu file):

| Giai đoạn | HDD | NVMe |
|---|---|---|
| Crawl (đã đo, tuyến tính theo số file) | ~20 phút | ~3 phút |
| Hash cascade | ~30–45 phút | ~5 phút |
| Decode + pHash toàn bộ | 3–6 giờ (I/O bound) | 20–40 phút (CPU bound) |
| Ghép cặp MIH + clustering | 2–5 phút | 2–5 phút |

Kết luận không đổi: trên HDD, **decode để pHash là chi phí thống trị** — gấp khoảng 10 lần
mọi giai đoạn khác cộng lại. Đây là chỗ đáng đầu tư tối ưu, không phải phần hash.

---

## 10. Roadmap

- **v0.1 — ĐÃ XONG.** Crawl → SQLite catalog. Resume được, định danh theo Volume GUID +
  NTFS FileId, lọc reparse point và cloud placeholder, long path. Xem §11 về những gì
  đã làm khác thiết kế ban đầu.
- **v0.2** — cascade exact duplicate (size → partial → full BLAKE3). Report HTML/CSV.
- **v0.3** — decode + dHash/pHash + MIH + clustering. Guard low-entropy.
- **v0.4** — chấm điểm bản giữ, quarantine + undo manifest, merge EXIF.
- **v0.5** — Flutter UI: duyệt cụm trùng trực quan, so sánh cạnh nhau, chọn thủ công.
- **v1.0+** (tuỳ chọn) — embedding CLIP cho crop/watermark; dedupe video.

---

## 11. v0.1 đã làm khác thiết kế ở đâu

**Chưa dùng USN Journal / MFT.** Thay vào đó dùng
`GetFileInformationByHandleEx(FileIdExtdDirectoryInfo)`. Lý do: một lời gọi trả về
attributes + size + timestamps + reparse tag + **FileId 128-bit** trong cùng một buffer,
khoảng 1 syscall cho mỗi ~1000 entry. Nó cho đúng thứ mà MFT được kỳ vọng mang lại —
định danh file mà không phải mở từng file — nhưng **không cần quyền admin**, và có
fallback (`FILE_FULL_DIR_INFO`) chạy được trên exFAT/FAT32 của ổ rời.

Đo thực tế 1.750 file/s trên HDD cache lạnh. USN vẫn nhanh hơn nữa nhưng đòi admin,
chỉ NTFS, và phải dựng lại đường dẫn từ parent FRN — chi phí đó chưa đáng ở v0.1.
Interface `DirectoryCrawler` để ngỏ cho USN gắn vào sau.

**Checkpoint lưu "frontier", không lưu "thư mục đã xong".** Cách sau nghe hợp lý nhưng
sai: đánh dấu một thư mục đã xử lý rồi bỏ qua nó khi resume sẽ khiến các thư mục con của
nó không bao giờ được đẩy vào stack — mất nguyên cả nhánh mà không báo lỗi gì. Nên bảng
`crawl_frontier` lưu danh sách thư mục *còn nợ*; một dòng chỉ bị xoá trong đúng
transaction đã chèn các thư mục con của nó. File và cập nhật frontier đi chung một
channel theo thứ tự, và writer chỉ commit tại ranh giới thư mục — nên mọi thời điểm crash
đều để lại catalog nhất quán.

**Crawl hiện đang đơn luồng.** Trên HDD đó gần như đã tối ưu (nhiều luồng chỉ làm tăng
seek). Trên SSD thì đang bỏ phí hiệu năng. Việc song song hoá theo `StorageKind` — đã
detect sẵn và lưu trong bảng `volumes` — để v0.2.

**Cảnh báo phát hiện được khi test trên máy này:** ổ `G:` là Google Drive gắn dưới dạng
ổ ảo FAT32. `GetDriveType` báo đây là ổ cố định chứ không phải remote, nên bộ phân loại
không tự nhận ra. Quét `G:\` sẽ stream dữ liệu qua mạng. Ổ như vậy cần loại trừ thủ công
cho tới khi v0.2 nhận diện được sync-root provider.
