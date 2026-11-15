# 测试

## 公共维护单元测试

`maintenance/tests` 下所有 `test_*.py` 都是公共、离线的合成夹具测试，可用一个命令运行：

```sh
python3 -B maintenance/run_unit_tests.py
```

运行器自动发现全部普通测试模块，新增模块无需登记；任何断言失败都会使命令以非零状态退出。CI 的 Linux 作业运行同一条命令，名称是 “public fixture maintenance checks”。

这条命令只覆盖公共维护夹具。Core 的包契约与相关行为测试由 `package-contracts` 工作流用 `dotnet test` 的过滤器运行，不在这里重复；两者都不代表 Core 或私有夹具的全量覆盖。

## 完整 Core 测试（Linux，公共生成输入）

`ci.yml` 的 `core-tests-linux` 作业运行 `tests/FreedomInstaller.Core.Tests` 的全部测试，不使用维护者过滤器。输入全部来自公共来源：主体由 `maintenance/prepare_test_inputs.py` 一次生成，BootTool 与公共 system 源码另由工作流从源码生成（见下）：

1. 调用 `build_installer.prepare(repo_root, work_dir)`（prepare-only 语义，不构建旧版可选 offline 资产，也不发布），生成 G4/G5/TB710FU 工具链、两个驱动 ZIP、固定版本的 Windows EDL/packer PE 和 `build-inputs.props`；
2. 在独立目录用 `cmake -DBUILD_SHIMS=OFF` 构建同一份 `tools/boot-packers` 源码，得到真正的 Linux ELF 测试 oracle（不设置 `CMAKE_SYSTEM_NAME`，不与 Windows 交叉编译混用）。

脚本把这些产物映射为测试强制要求的环境变量：`TEST_ARTIFACT_ROOT`、`EDL_CLIENT_TEST_WORK_ROOT`、`EDL_RAW_PACKER_PATH`/`EDL_RAW_UNPACKER_PATH`（实际固定的 Windows PE，Linux 上只读取、绝不执行）、`EDL_RAW_LINUX_PACKER_PATH`/`EDL_RAW_LINUX_UNPACKER_PATH`（实际原生 ELF）、`EDL_TOOLCHAIN_TEST_BUNDLE_PATH`、`EDL_TOOLCHAIN_G5_TEST_BUNDLE_PATH`、`ANDROID_DRIVER_TEST_ARCHIVE`、`QUALCOMM_DRIVER_TEST_ARCHIVE`，以及指向生成 props 的 `CORE_TEST_PROPS_PATH`。任一强制输入缺失都会让准备步骤失败，而不是跳过整个测试套件。PE 只校验开头的 `MZ` 签名（真实 Windows PE 后面还有更多内容），ELF 仍要求完整四字节魔数；环境文件按 shell 规则引用每个值，因此含空格或 shell 字符的路径可以直接 `set -a; . file`。

工作流还会生成两个公开、可再生成的输入，并在测试前检查它们确实存在：

- `BOOT_TOOL_DLL`：用与 Core 测试相同的 props 构建本仓库当前的 `FreedomInstaller.BootTool`，导出实际的 `net10.0` DLL（同目录的 `armada-boot-tool` apphost 供 Python 测试使用），不是私有夹具；
- `ARMADA_SYSTEM_SOURCE_PATH`：对公共 `ArmadaOS-Freedom/armadaos-freedom` 仓库做第二个 checkout（`ref: main`，无子模块、无 LFS、无额外大文件），指向其中的 `system_files` 源码，供 Python `boot_trust` 测试只读导入；
- `/usr/libexec/armada/kshim-tools/{MultiBootKernelPatcher,UnpackTool}`：CLI 只信任这两个固定安装路径，所以工作流在一次性 Linux runner 上把上面已校验的原生 ELF 以 `0755` 安装到该处（root 属主、普通文件、非符号链接，不使用 Windows PE）。这是 CI runner 的准备步骤，不是生产部署，CLI 的信任检查本身未改变；需要本机复现 `SystemUpdateBootToolTests` 的 `build-raw`/编排用例时，同样要先把这两个 ELF 装到这两个路径。

### 资源文件系统前置条件

`core-tests-linux` 需要远超托管 runner 默认可用空间的可写空间：按各自既有的清理契约，测试在运行期间同时保留多个数 GiB 的零填充归档和镜像，单个测试类即可占用数十 GiB。因此该作业在 `RUNNER_TEMP` 下用 `mktemp -d` 新建本次运行专属的稀疏镜像（128 GiB），`mkfs.btrfs` 后用 `-o loop,compress-force=zstd:1` 挂载，只把 `TEST_ARTIFACT_ROOT` 和 `EDL_CLIENT_TEST_WORK_ROOT` 覆盖为该卷内的子目录，其余环境变量、测试属性和不加过滤的运行方式保持不变。测试写入的字节内容、文件长度和路径与原来完全相同，物理占用只因零数据被压缩而下降；归档格式、测试输入、断言和清理契约均未改变。TRX 结果通过 `--logger trx --results-directory` 写到卷外的专属 `RUNNER_TEMP` 路径。作业结束时只卸载并删除本次运行自己创建的挂载、镜像和空目录，卸载失败则保留镜像并让作业失败。本机复现同样需要一个容量足够、支持压缩的可写文件系统，否则会因空间不足而失败。

### 外部固件夹具的正式跳过

少数测试需要本仓库无法再分发的输入：公开渠道的 G5 stock 固件（`G5_PUBLIC_ABL_PATH`、`G5_LOADER_FIXTURE_ROOT`），私有历史 EDL 日志（`EDL_CLIENT_LOADER_LOG_PATH`、`EDL_CLIENT_MISC_LOG_PATH`），以及私有的真实资产挂载（`MAINTENANCE_TEST_ASSET_ROOT`、`TF_NATIVE_PACKAGE_PATH`、`INTERNAL_INSTALL_MAINTENANCE_BOOT`、`INTERNAL_INSTALL_SUPPORT_ARCHIVE`、`INTERNAL_INSTALL_TOOLBOX`）。对应测试方法使用 `RequiredFixturesFact`/`RequiredFixturesTheory`（见 `RequiredFixtureAttributes.cs`），规则是：

- 该夹具族的全部变量都未设置或为空时，测试报告 SKIPPED，并明确写明“不是通过”；
- 只要提供了任意一个成员，测试就照常运行；不完整、格式错误或不匹配的输入仍由原有夹具校验器直接失败，绝不会被转成跳过。

因此 CI 的绿色只表示“公共输入覆盖的普通测试全部通过 + 已明确列出未提供的外部夹具”，不表示 G5 stock 固件或私有历史日志已被验证。

`BOOT_TOOL_DLL` 和 `ARMADA_SYSTEM_SOURCE_PATH` 不是私有夹具，也不参与上述跳过判定。依赖 G5 stock 固件的 CLI/Python 测试只由 `G5_PUBLIC_ABL_PATH`/`G5_LOADER_FIXTURE_ROOT` 这一族控制：CI 同时提供 BootTool 和公共 system 源码，不会让缺少 stock 固件的测试从跳过变成失败。

## 特权引导生命周期 harness

`maintenance/tests/test_bootstrap_lifecycle.py` 不是普通单元测试，也不属于上述通用 CI。它在一次性容器内执行真实的 BusyBox shell 夹具，需要：

- `TF_BOOTSTRAP_CONTAINER_TEST=1` 且处于容器环境；
- C# 的 `TfBootstrapScriptTests` 在设置 `TF_BOOTSTRAP_FIXTURE_EXPORT_DIR` 时导出的 `launcher.sh` 与 `cleanup.sh`；
- `TF_BOOTSTRAP_REAL_BUSYBOX` 指向真实 BusyBox 可执行文件；
- `TF_BOOTSTRAP_TEST_WORK` 指向 `/evidence` 下本次运行专属的新目录；
- 挂载用例还需要 `CAP_SYS_ADMIN`，缺少时报告 BLOCKED，绝不伪装通过。

这些前置条件不满足时 harness 会主动拒绝运行，因此不要把它接入通用作业，也不要在主机 runner 上直接运行。普通测试运行器会打印它的排除原因和上述要求；需要时按真实前置条件单独调用。

`ci.yml` 里有一个独立的 `tf-bootstrap-lifecycle` 作业：它在一次性的 `--privileged` Docker 容器中安装真实 BusyBox，先用 `TfBootstrapScriptTests` 把实际生成的 `launcher.sh`/`cleanup.sh` 导出到 `/evidence`，再原样调用上述 9 个用例。导出前会创建 `/evidence/tf-fixtures`、`TEST_ARTIFACT_ROOT` 和 `EDL_CLIENT_TEST_WORK_ROOT`；`TF_BOOTSTRAP_TEST_WORK` 使用本次运行专属的新目录并只创建不删除，清理由测试自身负责。该作业不复用普通维护测试作业，也不改变后者的自动发现范围。
