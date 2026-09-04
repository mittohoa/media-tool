# Winnow

*tìm ảnh trùng mà không xoá gì*

**[Tải bản cài Windows](https://github.com/mittohoa/media-tool/releases/latest/download/Winnow-Setup.exe)**
· [bản chạy thẳng (.zip)](https://github.com/mittohoa/media-tool/releases/latest/download/WinnowApp-win-Portable.zip)
· [trang giới thiệu](https://mittohoa.github.io/media-tool/)


Tìm ảnh trùng trên tập dữ liệu lớn, rải rác nhiều ổ đĩa, trên Windows.

Bắt được cả bản trùng **đã bị xoá metadata** — thứ mà mọi tool dựa trên hash file đều
bỏ sót, vì strip EXIF làm đổi hoàn toàn hash của file.

Thiết kế đầy đủ: [`docs/dedupe-design.md`](docs/dedupe-design.md)

## Trạng thái

| Version | Nội dung | Trạng thái |
|---|---|---|
| v0.1 | Crawl + SQLite catalog, resume, multi-volume | **xong** |
| v0.2 | Cascade trùng byte (size → head/tail → xxHash128) | **xong** |
| v0.3 | Decode WIC + pixel hash + dHash/pHash + Multi-Index Hashing | **xong** |
| v0.4 | Chọn bản giữ, quarantine + undo, đọc EXIF, **merge EXIF** | **xong** |
| v0.5 | App desktop WPF: chọn phạm vi quét, chạy pipeline, duyệt cụm | **xong** |
| — | Icon, menu chuột phải Explorer, shortcut | **xong** |

## Tiêu chí "trùng" — ba tầng

| # | Điều kiện | Bắt được | Sai sót |
|---|---|---|---|
| 1 | `size` bằng nhau **và** `xxHash128(file)` bằng nhau | Copy y hệt | 0 |
| 2 | `xxHash128(pixel đã decode)` bằng nhau | **Cùng ảnh, mất metadata** | 0 |
| 3 | `Hamming(dHash) ≤ 4` hoặc `Hamming(pHash) ≤ 4`, **và** thumbnail khớp, **và** không có hai giờ chụp khác nhau | Resize, nén lại, chỉnh sáng | có ngưỡng |

Điều kiện cuối của tầng 3 quan trọng hơn vẻ ngoài của nó: ảnh chụp liên tiếp cách nhau vài
giây giống hệt nhau dưới mọi hash tri giác, nhưng máy ảnh đã ghi hai thời điểm khác nhau —
và copy/nén lại/resize thì không bao giờ bịa ra giờ chụp mới. Trên thư viện thật, điều kiện
này loại **593.318 cặp** mà vẫn giữ nguyên toàn bộ 16.300 ca "mất metadata".

Tầng 2 là tầng giải quyết bài toán "trùng nhưng thiếu metadata": xoá EXIF không đụng một
pixel nào, nên hai file khác hẳn về byte lại decode ra **đúng cùng một buffer pixel**.
Chi tiết và số liệu nghiệm thu: [`docs/algorithm-decision.md`](docs/algorithm-decision.md).

## Build

```
dotnet build
```

Cần .NET SDK 9. Binary ra ở `src/MediaTool.Cli/bin/Debug/net9.0-windows/mediatool.exe`.

## Dùng

```bash
# Xem các ổ đang gắn: GUID định danh, HDD hay SSD, có FileId hay không
mediatool volumes

# 1. Quét (mặc định chỉ lấy file ảnh, gồm cả RAW)
mediatool scan E:\Photos F:\Backup

# Ngắt bằng Ctrl+C rồi chạy lại đúng lệnh cũ để tiếp tục từ chỗ dừng
mediatool scan E:\Photos F:\Backup

# 2. Tầng 1 - trùng byte
mediatool hash
mediatool duplicates --csv dupes.csv

# 3. Tầng 2+3 - trùng ảnh (bắt bản mất metadata)
mediatool images                      # decode 1 lần, sinh 4 chỉ số
mediatool metadata                    # doc EXIF - BAT BUOC truoc 'similar'
                                      # gio chup la thu phan biet anh chup lien tiep
                                      # voi ban trung that
mediatool similar --csv similar.csv   # gom cụm + in histogram để chỉnh ngưỡng
mediatool similar --pixel-only        # chỉ lấy ca chắc chắn 100%

# 4. Quyet dinh + hanh dong
mediatool merge-exif --plan plan.csv --quarantine Q --execute
                                                # CUU ngay chup truoc khi xoa gi
mediatool plan --pixel --prefer "Anh cua toi"   # lap ke hoach, khong dong file nao
mediatool apply --plan plan.csv --quarantine Q  # chay thu, chi xac minh
mediatool apply --plan plan.csv --quarantine Q --execute   # chuyen file
mediatool undo --batch 20260901-090842          # tra lai het

# Tóm tắt catalog + chi phí thật của tầng lọc trùng
mediatool stats

# Xem tool đọc được một file cụ thể hay không, và vì sao không
mediatool probe "E:\Photos\anh.heic"
```

Catalog mặc định nằm ở `%LOCALAPPDATA%\media-tool\catalog.db`, đổi bằng `--db`.

### Tuỳ chọn scan

| Cờ | Ý nghĩa |
|---|---|
| `--ext .jpg,.png` | chỉ những đuôi này |
| `--all` | mọi file, không chỉ ảnh |
| `--min-size 500KB` | bỏ file nhỏ hơn (mặc định 16KB) |
| `--include-cloud` | đọc cả file placeholder — **đọc cảnh báo nó in ra** |
| `--include-hidden` | không bỏ qua file/thư mục ẩn |
| `--no-resume` | quét mới thay vì tiếp tục lần dở dang |
| `--all-drives` | quét mọi ổ cục bộ; in rõ ổ nào lấy, ổ nào bỏ và vì sao |
| `--include-cloud-drives` | cho phép quét cả ổ ảo Google Drive/OneDrive (mặc định **không**) |
| `--exclude <text>` | bỏ thư mục có đường dẫn chứa chuỗi này, lặp lại được |

### Hai trục phạm vi, đừng nhầm

**Lúc quét** quyết định cái gì vào catalog — `scan E:\Photos F:\` hoặc `scan --all-drives`,
kèm `--exclude`. Đây là bước đắt (hàng giờ), nên làm **rộng, một lần**.

**Lúc phân tích** quyết định lấy phần nào ra dùng — `--under` và `--exclude` trên
`similar`, `plan`, `metadata` và cả app. Đây là bước rẻ, nên cắt lát thoải mái **mà không
phải quét lại**.

Ví dụ thật: thư viện 87k ảnh có lẫn ảnh website. Thêm `--exclude _mvc` khi phân tích làm
số cụm phải duyệt giảm từ 4.130 xuống 619, mà dung lượng thu hồi chỉ giảm từ 40,1 GB xuống
30,1 GB. Không cần quét lại một file nào.

## Màn duyệt: bảng so sánh và bản đồ khác biệt

Câu khó nhất khi duyệt gần-trùng không phải "hai ảnh này có giống nhau không" — đặt cạnh
nhau thì trông giống hệt. Câu khó là **một tấm lưu hai lần, hay hai tấm chụp cách nhau một
giây**. Khác biệt nằm ở những mảng nhỏ mà mắt lướt qua.

App trả lời bằng hai thứ đặt cạnh mỗi cụm:

**Bảng so sánh** xếp thuộc tính theo hàng ngang — độ phân giải, ngày chụp, máy ảnh, chất
lượng, định dạng, dung lượng — và tô hổ phách ô thắng của từng hàng. Đọc một hàng là biết
ngay vì sao bản này được giữ, thay vì phải tự ghép lại từ ba khối chữ rời rạc.

**Bản đồ khác biệt** trừ hai ảnh cho nhau và vẽ chỗ chênh lệch. Điều đọc được không phải
lượng chênh mà là **hình dạng** của nó:

| Hình dạng | Nghĩa |
|---|---|
| tối gần hết | không thấy khác biệt ở cỡ duyệt |
| rải đều khắp khung, bám theo chi tiết ảnh | nén lại hoặc thu nhỏ — cùng một khoảnh khắc |
| dồn cục một vùng | có gì đó đã dịch chuyển — nhiều khả năng là khung hình khác |
| viền sáng, giữa nguyên | một bản là ảnh cắt của bản kia |

Chỉ số quyết định là **độ tập trung**: phần chênh lệch nằm trong 10% ô "ồn" nhất. Codec rắc
lỗi đều tay nên 10% ồn nhất chỉ giữ hơn một phần mười tổng số một chút. Người dịch vai giữa
hai lần bấm máy thì dồn gần hết vào mấy ô họ chiếm. **Cùng một lượng chênh lệch mang hai ý
nghĩa trái ngược tuỳ cách nó phân bố** — đó là lý do độ lớn một mình không quyết được, và
là điều `DifferenceMapTests` kiểm trực tiếp.

Độ sáng được khớp trước khi trừ, nếu không một bản chỉ bị chỉnh sáng sẽ cháy cả bản đồ và
bị đọc nhầm thành ảnh khác hẳn.

## Quét xong rồi thì làm gì

App chia việc thành **hai luồng tách hẳn**, vì chúng khác nhau về bản chất:

**1. Nhóm không có gì phải quyết — nút `Apply N identical`.**
Mọi bản trong nhóm giải mã ra cùng một ảnh, chỉ khác byte vỏ (ca bị xóa metadata). Không
có phán đoán nào ở đây nên chúng không vào danh sách duyệt. Đây là **phần lớn dung lượng**:
trên thư viện này là 15.813 file / 26,9 GB. Trước mỗi lần chuyển, hai file được giải mã
lại và so pixel; khác một pixel là từ chối.

**2. Nhóm cần bạn nhìn — nút `Apply`.**
Khác độ phân giải, khác chất lượng nén, hoặc có thể là hai khoảnh khắc khác nhau. Phải
duyệt từng cụm: `1`–`9` chọn bản giữ nếu gợi ý sai, rồi **`Enter` để xác nhận** và sang
cụm sau; `S` để bỏ qua. Đếm ở góc dưới phải cho biết đã sẵn bao nhiêu. `Apply` chỉ động
vào những cụm đã `Enter`.

Hai nút không gộp làm một là có chủ ý: một cú bấm định bụng cho hai quyết định vừa duyệt
không được phép lặng lẽ chuyển mười sáu nghìn file.

Cả hai đều chạy thử khô trước, hiện số liệu thật rồi mới hỏi, và **chuyển chứ không xóa** —
xem `## Vòng đời một file bị coi là thừa`.

## Quyết định duyệt được lưu lại

Duyệt gần-trùng là phần duy nhất máy không làm thay được, nên mỗi quyết định là thứ đắt
nhất trong catalog. Trước đây chúng chỉ nằm trong RAM: đóng cửa sổ là mất sạch, và vì Apply
chỉ động vào cụm đã xác nhận nên mất một cách **im lặng** — nút quay về trạng thái
"không có gì để làm".

Nay mỗi lần bấm `Enter` hoặc `S` là ghi ngay xuống catalog (bảng `review_decisions`, schema v7).
Mở lại app thì khôi phục cả trạng thái lẫn **bản bạn đã chọn giữ**.

Cụm được định danh bằng **tập file trong nó**, không phải vị trí trong danh sách — danh sách
đổi thứ tự mỗi khi đổi phạm vi, còn tập file bạn đã nhìn thì không. Hệ quả có chủ ý: cụm
nào đã thay đổi thành viên thì quyết định cũ **không còn áp dụng**, vì nó đã là cụm khác.

```
winnow-cli review            xem đã lưu những gì
winnow-cli review --clear    xóa hết để duyệt lại từ đầu (không đụng file nào)
```

## Catalog nằm ở đâu

`%LOCALAPPDATA%\Winnow\catalog.db`. Nếu chưa có file đó nhưng có
`%LOCALAPPDATA%\media-tool\catalog.db` (tên cũ, trước khi app được đặt tên Winnow) thì
dùng file cũ và **ghi rõ trên header** là đang mở file nào. Không tự di chuyển gì cả —
catalog tốn hàng giờ để dựng lại nhưng rất rẻ để tìm.

Đây từng là lỗi thật: app và CLI nhìn hai thư mục khác nhau, nên mở app từ shortcut ra
thư viện rỗng — trông hệt như mất sạch công quét. `--db <file>` vẫn đè được.

## Nới ngưỡng có được gì không?

`similar` in sẵn biểu đồ khoảng cách để bạn tự hiệu chuẩn. Đo thật trên thư viện 87k
này (`--exclude _mvc`, 37.887 ảnh đã giải mã):

| Ngưỡng | Thời gian | Cặp xác nhận | File thừa | Thu hồi | Cặp bị giờ chụp bác bỏ |
|---|---|---|---|---|---|
| `--hamming 4 --mae 8` (mặc định) | 3,4s | 515 | 16.981 | 28,2 GB | 632.462 |
| `--hamming 6 --mae 12` | 81s | 689 | 17.087 | 28,5 GB | 5.799.444 |
| `--hamming 10 --mae 16` | >23 phút, phải dừng | — | — | — | — |

Nới từ 4 lên 6 tốn **24 lần thời gian** để đổi lấy **0,3 GB (~1%)**. Lên 10 thì không
chạy nổi: chỉ mục đa tầng chia 64 bit thành `hamming + 1` dải, nới ngưỡng làm dải ngắn
lại, xô hạt vào chung rổ và chi phí thành bình phương.

Cột cuối mới là cột đáng lo: số cặp **trông giống nhau nhưng giờ chụp khác** tăng 9 lần.
Ở ngưỡng rộng, thứ giữ cho kết quả không sai gần như hoàn toàn là EXIF, không phải thuật
toán thị giác. Mà còn **5.070 ảnh không có giờ chụp** — với chứng ấy thì không có gì chặn.
Vì vậy mặc định giữ chặt, và `apply` từ chối nhóm near-duplicate trừ khi bạn duyệt tay.

Biểu đồ khoảng cách thumbnail của thư viện này **không có khe** — nó thoải dần từ 89 cặp
xuống vài cặp chứ không tách hẳn. Nghĩa là ở đây không tồn tại một con số "đúng", chỉ có
lựa chọn giữa bỏ sót và báo nhầm.

## Chọn bản nào để giữ

Không cộng dồn điểm, mà **so sánh theo thứ bậc** — tầng dưới chỉ được phá thế hoà của tầng trên:

1. **Độ phân giải** (khi lệch >20%) — mất pixel là mất vĩnh viễn
2. **Ngày chụp EXIF** — thứ mà bản bị strip đã mất và không bản nào cấp lại được
3. **Độ đầy đủ EXIF**
4. **Chất lượng nén** — bản lưu lại bao giờ cũng lượng tử hoá nặng hơn bản gốc
5. Vị trí thư mục, tên file (`--prefer`)
6. Đường dẫn, chỉ để kết quả ổn định giữa các lần chạy

Lý do không cộng dồn: với điểm cộng, một thư mục ưu tiên cộng thêm cái tên đẹp có thể
**đè được** sự thật là bản kia còn ngày chụp còn bản này thì không. Đây là lỗi thật đã xảy
ra trong lúc phát triển, test bắt được — xem `docs/algorithm-decision.md`.

## Vòng đời một file bị coi là thừa

```
plan      -> ghi ra CSV, khong dong file nao. Ban doc va sua duoc cot action
apply     -> chay thu; --execute moi CHUYEN file vao quarantine (khong xoa)
history   -> xem con bao lau nua moi duoc purge, va lenh de dua tat ca tro lai
undo      -> tra file ve dung cho cu, bat cu luc nao truoc khi purge
purge     -> XOA HAN. Chi sau thoi gian cho, va phai go dung ten batch
```

Mặc định **chờ 14 ngày** trước khi được phép purge (`--retention 30d` để đổi). Trong suốt
thời gian đó `undo` luôn hoạt động. Không có gì tự động xoá — `purge` phải gõ tay.

Nếu mất catalog, undo vẫn chạy được từ `manifest.csv` nằm ngay trong thư mục quarantine:

```
mediatool undo --manifest <quarantine>\<batch>\manifest.csv
```

## Không muốn xoá gì cả? Dùng hardlink

Với ảnh **trùng byte trên cùng một ổ NTFS**, có lựa chọn khác hẳn: biến bản trùng thành
**hardlink** trỏ vào bản gốc. Hai đường dẫn vẫn còn nguyên, vẫn mở được, vẫn hiện trong
thư mục — nhưng đĩa chỉ lưu nội dung một lần.

```
mediatool plan --exact --out plan.csv
mediatool hardlink --plan plan.csv --quarantine Q --execute
mediatool hardlink --undo link-20260901-122626
```

Kiểm chứng bằng `fsutil hardlink list <file>` — nó liệt kê mọi đường dẫn cùng trỏ vào một
file vật lý.

**Dung lượng chỉ thật sự được thu hồi sau khi `purge`.** Bản gốc vẫn nằm trong quarantine
để undo được, và nó vẫn chiếm chỗ. Trình tự đầy đủ:

```
hardlink --execute   ->  hai duong dan cung tro vao mot file, ban goc cat trong quarantine
                         (dung luong CHUA doi - ban goc van con)
history              ->  dem nguoc 14 ngay
purge --execute      ->  xoa ban goc trong quarantine
                         (LUC NAY dung luong moi giam, va ca hai duong dan VAN mo duoc)
```

Đây chính là chỗ hardlink hơn quarantine thường: sau `purge`, với quarantine thường thì một
đường dẫn biến mất; với hardlink thì **cả hai vẫn còn**.

**Hai điều phải biết trước khi chọn cách này:**
- Sửa **tại chỗ** một đường dẫn sẽ đổi cả hai, vì chúng là một file. Phần lớn phần mềm ảnh
  ghi ra file mới chứ không sửa tại chỗ, nhưng không phải tất cả.
- Timestamp riêng của bản trùng bị thay bằng của bản gốc.

Chỉ áp dụng cho `--exact` (trùng byte), chỉ trong **cùng một ổ**, chỉ NTFS/ReFS. Bản gốc
vẫn được giữ trong quarantine trước khi tạo link, nên hoàn tác được đầy đủ.

**So sánh hai cách:**

| | quarantine + purge | hardlink + purge |
|---|---|---|
| Dung lượng thu hồi | như nhau | như nhau |
| Sau khi purge | một đường dẫn **biến mất** | **cả hai đường dẫn vẫn mở được** |
| Rủi ro | mất đường dẫn nếu chọn sai | sửa tại chỗ ảnh hưởng cả hai |
| Phạm vi | mọi ổ, mọi tầng | cùng ổ, NTFS, chỉ tầng trùng byte |

## Cứu metadata trước khi xoá

Ca thường gặp: bản đáng giữ (to hơn, nét hơn) lại là bản đã bị editor hoặc app nhắn tin
**xoá sạch EXIF**, còn bản sắp bị loại thì vẫn giữ ngày chụp. Xoá thẳng là mất vĩnh viễn
thông tin duy nhất còn lại về thời điểm chụp.

`merge-exif` chuyển khối EXIF sang bản giữ **trước** khi `apply` động vào gì:

```
mediatool plan --similar --out plan.csv
mediatool merge-exif --plan plan.csv --quarantine Q --execute   # cuu metadata
mediatool apply --plan plan.csv --quarantine Q --execute        # roi moi don dep
```

Đây là **thao tác cắt-ghép ở mức byte**, không decode, không nén lại. JPEG gồm các khối
metadata rồi tới dữ liệu ảnh nén, hai phần không chồng lên nhau — nên thay khối metadata là
copy nguyên xi phần còn lại. Đo trên ảnh thật:

```
truoc:  pixel A057F9BFE34E425FE7B5233096A0B2A7   exif none
sau:    pixel A057F9BFE34E425FE7B5233096A0B2A7   exif 48 tags, 2025-02-05 07:53, SONY ILCE-7M4
```

Pixel hash **y hệt** — không một điểm ảnh nào đổi.

Đây cũng là đường **duy nhất** ghi vào ảnh của bạn, nên nó theo đúng nguyên tắc của phần
xoá: không bao giờ ghi đè. File mới được tạo dưới tên tạm, xác minh (phải decode ra **đúng
pixel hash cũ** và phải có ngày chụp mới), rồi bản gốc mới được chuyển vào quarantine và
file mới thế chỗ. `undo --batch` trả lại nguyên trạng.

## Bốn lớp bảo vệ khi xoá

1. `plan` không đụng file nào, chỉ ghi ra CSV để bạn đọc và **sửa** (cột `action`)
2. `apply` mặc định chạy thử; phải có `--execute` mới động vào file
3. Trước mỗi lần chuyển, file được **xác minh lại trên đĩa ngay lúc đó** — trùng byte thì
   so từng byte, trùng pixel thì decode lại so pixel hash. Hash lưu từ lần quét trước chỉ
   là lời khai về quá khứ, không phải bằng chứng hiện tại
4. File được **chuyển vào quarantine, không xoá**, kèm manifest; `undo --batch` trả lại hết
5. Xoá hẳn chỉ qua `purge`, sau thời gian chờ, và **từ chối mọi đường dẫn nằm ngoài thư
   mục quarantine** — nên kể cả bản ghi trong DB bị hỏng hay bị sửa, nó cũng không thể
   chạm tới ảnh gốc

Nhóm có bản nằm trên ổ đang tháo ra thì bị bỏ qua — không thể xác minh, và "chắc đâu đó
còn bản khác" không phải căn cứ để đụng vào file.

## Ba điều v0.1 làm khác các tool dedupe thông thường

**Định danh ổ bằng Volume GUID, không bằng chữ cái ổ.** Rút ổ ngoài ra cắm lại thường
đổi letter. Catalog neo theo GUID nên ổ giữ nguyên danh tính, và **ổ đang tháo ra vẫn
nằm trong catalog** — vẫn tham gia được vào quyết định trùng lặp sau này.

**Không đụng vào file cloud placeholder.** File OneDrive/Dropbox/Google Drive chưa tải
về mang cờ `FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS`; đọc nó là kích hoạt tải. Trên thư
viện vài TB, một lần quét vô ý sẽ biến thành cuộc tải về hàng terabyte. Mặc định bỏ qua,
và đếm lại cho bạn biết đã bỏ bao nhiêu.

**Resume là thật, không phải chạy lại từ đầu.** Bảng `crawl_frontier` lưu danh sách thư
mục *còn nợ*; mỗi dòng chỉ bị xoá trong đúng transaction đã chèn các thư mục con của nó.
Writer chỉ commit tại ranh giới thư mục, nên crash ở bất kỳ đâu — kể cả rút ổ giữa
chừng — đều để lại catalog nhất quán.

## Test

```
dotnet test
```

170 test. Trọng tâm không rải đều — phần lớn nằm ở đường **có thể mất ảnh**:

| Nhóm | Test | Bảo vệ điều gì |
|---|---|---|
| `SafetyInvariantTests` | 6 | Quét chính source theo danh sách cho phép khai báo sẵn: từng file được phép bao nhiêu `File.Delete` / `File.Move` / ghi đè, kèm lý do. Thêm một đường đụng vào file mà không khai báo là test đỏ ngay |
| `QuarantineLifecycleTests` | 16 | Chuyển → undo → purge trên file thật, gồm cả các ca phá hoại: cặp bịa, keeper biến mất, file đổi sau khi duyệt, bản ghi trỏ ra ngoài quarantine |
| `GuardAndHashTests` | 14 | Tách ảnh chụp liên tiếp khỏi bản trùng; bất biến xoay; bất biến độ sáng; escape SQL của bộ lọc phạm vi |
| `KeeperPolicyTests` | 10 | Metadata không được để vị trí thư mục đè; RAW không được thua JPEG; kết quả ổn định giữa các lần chạy |
| `MetadataTests` | 8 | Đọc EXIF từ file tự dựng, gồm sub-second; file hỏng/cụt không làm chết bộ đọc |
| `ExifMergeTests` | 11 | Merge không đổi pixel; không ghi đè; file tạm sẵn có không bị đè; undo được |
| `PlanCsvTests` | 6 | File plan bạn sửa tay: dấu phẩy/nháy trong đường dẫn, hành động gõ sai bị bỏ qua chứ không đoán |
| `HardlinkTests` | 8 | Cả hai đường dẫn còn nguyên; cặp không giống nhau bị từ chối; chạy lại là no-op; undo được |
| `IdenticalPictureApplyTests` | 5 | Đường chuyển nhiều file nhất: giải mã lại trước khi chuyển, từ chối khi plan sai hoặc file bị thay sau khi lập plan, undo được |
| `HardlinkReadOnlyTests` | 3 | File read-only vẫn phải lùi được; cờ read-only được giữ nguyên; xóa cờ không thành giấy phép đè file của người khác |
| `UndoCatalogStateTests` | 2 | Undo trả file về cả trên đĩa lẫn trong catalog, không để hai bên lệch nhau |
| `ReviewDecisionTests` | 6 | Quyết định duyệt sống qua lần đóng app; cụm được định danh bằng thành viên chứ không bằng vị trí |
| `CatalogScopeTests` | 7 | Phạm vi `--under` không được âm thầm rộng ra: `F:` chỉ là ổ F, không lấn sang ổ khác |
| `CatalogLocationTests` | 4 | Tìm ra catalog cũ do phiên bản trước để lại, thay vì mở rỗng rồi để người dùng tưởng mất dữ liệu |
| `ShellPathTests` | 5 | Shortcut và menu chuột phải trỏ đúng bản build đã cài, không lẫn Debug với Release |
| `DifferenceMapTests` | 10 | Hình dạng chênh lệch tách được nén lại khỏi khung hình khác ngay cả khi độ lớn ngang nhau; đổi độ sáng không bị đọc nhầm; bản đồ tối khi kết luận là không thấy khác biệt |
| `FilenameSourceTests` | 9 | Lấy giờ chụp từ tên file app (Android/WhatsApp/Messenger/Viber/Telegram/Screenshot) |

Mỗi test tương ứng một lỗi đã thật sự xảy ra trong quá trình làm, không phải test cho có.

## Đã đo

Quét toàn bộ một ổ HDD NTFS 325 GB:

```
908.000 file  |  261 GB  |  363.000 thư mục
8 phút 11 giây, cache lạnh  (~1.750 file/s)
catalog 424 MB  |  FileId phủ 100%
```
