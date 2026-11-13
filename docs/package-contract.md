# 安装包与尾部恢复区

## 职责

[armadaos-freedom](https://github.com/ArmadaOS-Freedom/armadaos-freedom) 负责 Linux 镜像、型号适配、启动器和 9008 系统包生产。本仓库负责 Windows 交互、设备与备份校验、实际布局规划、A/B 引导处理、9008 写入和回读。

系统包的当前格式、参数和构建入口以[系统仓库的打包文档](https://github.com/ArmadaOS-Freedom/armadaos-freedom/blob/main/docs/edl-package.md)为准：统一包 `bundle.json` schema 2 将共用内容存放一次；各型号 `package.json` schema 2 声明 `edl-raw-v1` 的压缩分区镜像及型号运行时。导入器检查声明、长度、哈希和目标型号。

安装器 0.7.32 起接受 `ghcr.io/armadaos-freedom/armadaos-freedom` 的签名来源声明，并兼容旧 `ghcr.io/mydanyi/armadaos-freedom` 包；两者均只接受 `stable`、`beta`、`testing` 及格式正确的可选镜像摘要。新包应使用组织地址，旧包的来源记录不被改写。

## ARMADA_BACKUP

恢复区由安装器使用目标平板本机备份生成，不由通用镜像携带其他设备的备份。固定长度为 2147483648 字节。新备份及新建恢复镜像均排除 userdata、rawdump、metadata 的分区内容，分区表中的布局信息仍保留。复用旧电脑备份时会校验原文件并保留原始清单，但不将其中的 metadata 内容放进新恢复镜像；电脑上的原备份不被改写。

| 区域 | 内容 |
| --- | --- |
| 前 64 KiB | JSON 头、布局和压缩载荷校验信息 |
| 紧接头部 | gzip 压缩的 tar：原始恢复文件、清单、安装记录与 GPT |
| 中间剩余部分 | 零填充 |
| 最后 64 KiB（v3） | 4 KiB 不可变描述符、两个 4 KiB 可变设置槽及保留区 |

v3 的描述符绑定恢复分区 UUID、安装 UUID、型号和载荷边界。恢复校验仍验证压缩载荷、描述符及保留区；设置槽可被启动器交替更新，不能因此将完整恢复资料判定为损坏。v2 则继续要求载荷后的区域全部为零。

完整字节协议及各型号启动器支持范围见[启动器存储设置](https://github.com/ArmadaOS-Freedom/armadaos-freedom/blob/main/devices/lenovo-tb323fu/boot/kshim-success-source/docs/storage-settings.md)。目录名不代表所有型号都启用了相同功能。TB710FU 使用启动器设置区型号编号 3（kShimC `device_board` 枚举中的空闲值；不认识它的旧版 kShimC 按未知型号处理）。

对应代码：`InternalInstallArtifacts` 负责生成和验证，`BootSettingsRegion` 定义 v3 设置区，`InternalInstallPreparationContext` 在写入前绑定备份与安装计划。`maintenance/BackupSettingsUpgrade` 是旧 v2 镜像的离线升级准备工具，仅输出经校验的修改区域，不直接刷写设备。

## 离线升级启动策略

离线升级的 `info` 响应必须包含字符串字段

```json
"bootPolicy": "dual-boot-no-slot-control"
```

该策略的含义：

- A/B 两个物理引导候选启动**同一份** Linux 部署；安装器只构建、上传并写入这两个候选，不重新分区，不初始化。
- 安装器和平板上的升级组件**不写槽位元数据、不选择槽位、不标记成功槽位**。Android 自己拥有活动、选中和成功槽位的唯一决定权；本仓库不实现也不修改任何引导程序或安全绕过。
- 安装器在创建会话、上传、预处理、执行和验证重启之前校验该字段。缺失该字段、类型不是字符串或字符串不完全相同，都会在任何写入请求发出之前被拒绝。只读的 `info`、预检、状态查询和恢复资料导出不要求该字段，因此旧组件的已保存会话仍可查看；要执行升级则必须先更新平板上的升级组件。

工厂空的非当前槽位只接收项目引导候选：它使用当前槽的原厂材料构建，不补齐该槽的 Android 系统，其原始零填充声明、Android 载荷和签名保持不变。此时两个候选仍使用同一份 Linux 载荷。机型范围为 TB322FC/TB323FU；TB710FU 的既有安装、维护和写入路线不因本策略改变。

对应代码：`OfflineUpdateBootPolicy` 定义唯一策略字符串，`OfflineUpdateTransfer` 在所有写入步骤前校验，`OfflineUpdateUiState` 据此禁用准备、执行和重启并提示只读状态。

## 双槽镜像运行时标记

TB322FC/TB323FU 的新系统双槽策略要求目标运行时二进制保留精确字面量
`ARMADA_LINUX_MIRROR_V1`。安装包的 `runtime` 角色在导入校验时检查该标记；
安装器的原始引导候选构建在调用原生打包工具之前再次检查同一字节。缺少标记的旧运行时、旧签名包或回滚目标会被拒绝，而不会静默继承可能仍会控制 Android 槽位的旧行为。

该检查只证明运行时来自带标记的构建，不构成签名、设备或启动授权。TB710FU
和 TF 卡包不在该策略范围内，旧部署的只读信息查询也不受影响。
