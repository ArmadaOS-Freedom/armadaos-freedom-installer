# 厂商预编译素材

这里只保存构建安装器所需的通用加载器与未修改驱动文件，不包含设备分区备份、校准资料、个人数据或系统镜像。构建时逐项核对 maintenance/build-inputs.json 中的长度与 SHA-256。

- g4/：TB322FC 四代的 Firehose，来源固件 TB322FC_CN_OPEN_USER_Q00041.1_W_ZUXOS_1.5.10.259_ST_260611；以及一个可接受 AOSP 签名链的 ABL，来源固件 TB322FC ZUXOS 1.1.11.188，固定长度 1048576 字节、SHA-256 `6C2DD7CA087C4481B9B3970E8251C4E09846ED85FE1A279EB8A2F7E637B3FF15`。该 ABL 不作为特定固件版本绑定镜像使用，只在实时 ABL 不接受 AOSP 签名链时按槽位替换。
- g5/：TB323FU 五代 QCTool 2026.3.21-1 加载文件，包含 Sahara XML 和它引用的八个镜像，完整保留，不与四代混用。
- tb710fu/：TB710FU（小新 Pad Pro GT，SM8650）的 Firehose，取自 ZUXOS 1.5.04.495 免解锁补充包的 EDL/xbl_s_devprg_ns.melf（与该机 9008 线刷包 bin/9008/prog_firehose_ddr-TB710FU.melf 为同一文件）；以及一个可接受 AOSP 签名链的 ABL `abl_aosp_capable.img`：CN ZUI 17.0.04.279 的 abl 按 1 MiB 分区补 0，与免解锁补充包的 abl.img 逐字节相同，联想证书签名，固定长度 1048576 字节、SHA-256 `6CFA16E65C2DADD0FC557DD356E6C383C693B4C3B7F0A651D92BB9F6E38B74A8`。联想官方原厂 ZUXOS 1.5.04.495 的 ABL 虽然带一份 AOSP 公钥，但有效密钥是联想自己的，锁着 BL 时不接受 AOSP 签名链；只在实时 ABL 的有效密钥不是 AOSP 时按槽位替换。
- driver/：Qualcomm USB Serial Driver 2.1.5.2，日期 2023-08-24，AMD64，仅保留 qcser.inf、qcser.cat、serial/amd64/qcusbser.sys；文件取自 Windows DriverStore，目录签名为 Microsoft Windows Hardware Compatibility Publisher。

这些预编译组件以原件随仓库提供，保留原始文件、签名与随包声明。来源、版本和文件哈希随源码维护，构建直接读取本目录。
