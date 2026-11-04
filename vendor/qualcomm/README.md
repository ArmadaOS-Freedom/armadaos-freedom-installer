# 厂商预编译素材

这里只保存构建安装器所需的通用加载器与未修改驱动文件，不包含设备分区备份、校准资料、个人数据或系统镜像。构建时逐项核对 maintenance/build-inputs.json 中的长度与 SHA-256。

- g4/：TB322FC 四代的 Firehose，来源固件 TB322FC_CN_OPEN_USER_Q00041.1_W_ZUXOS_1.5.10.259_ST_260611。
- g5/：TB323FU 五代 QCTool 2026.3.21-1 加载文件，包含 Sahara XML 和它引用的八个镜像，完整保留，不与四代混用。
- driver/：Qualcomm USB Serial Driver 2.1.5.2，日期 2023-08-24，AMD64，仅保留 qcser.inf、qcser.cat、serial/amd64/qcusbser.sys；文件取自 Windows DriverStore，目录签名为 Microsoft Windows Hardware Compatibility Publisher。

这些预编译组件以原件随仓库提供，保留原始文件、签名与随包声明。来源、版本和文件哈希随源码维护，构建直接读取本目录。
