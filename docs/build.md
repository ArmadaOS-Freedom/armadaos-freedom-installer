# 从源码构建安装助手

本仓库包含修改版工具的源码、构建脚本、上游版本关系，以及必要的厂商预编译文件。默认流程不下载本项目的依赖合集 Release，也不需要维护者电脑上的文件。

## 一次完整构建

安装 Docker 并使用 Linux 容器。单 EXE 发布还需要系统仓库生成的离线更新组件（见下节），先把它们放到构建主机的 `artifacts/offline/`，再在仓库根目录运行以下两条命令；Linux shell 和 Windows PowerShell 均可使用：

```sh
docker build -f tools/installer-builder.Dockerfile -t armada-installer-builder tools
docker run --rm -v "${PWD}:/src" -v "${PWD}/artifacts:/work" armada-installer-builder python3 -B maintenance/build_installer.py --work-dir /work/build --output-dir /work/publish --offline-bootstrap-script /work/offline/armada-offline-bootstrap --offline-updater-bundle /work/offline/armada-offline-updater.tar.gz
```

产物为 `artifacts/publish/FreedomInstaller.App.exe`，Windows x64 自包含单 EXE。最终用户不需要另装 .NET SDK；Linux 系统镜像仍由系统仓库单独生成。

首次构建需要联网下载官方组件和 NuGet 包。后续构建复用经过哈希校验的下载缓存，每次仍从源码编译修改版工具。构建镜像固定为 .NET SDK 10.0.402 / Ubuntu 24.04 AMD64，安装 CMake、Make、Python 和 MinGW POSIX 编译器。

已有相同工具链的 Linux 环境也可运行：

```sh
python3 -B maintenance/build_installer.py --work-dir /work/build --output-dir /work/publish \
  --offline-bootstrap-script /work/offline/armada-offline-bootstrap \
  --offline-updater-bundle /work/offline/armada-offline-updater.tar.gz
```

工作目录和输出目录应使用绝对路径。编译产物、缓存和日志不应提交到源码仓库。

### 离线更新组件

单 EXE 发布要求两个本地已有文件，均以固定资源名原样嵌入：

- `armada-offline-bootstrap`：独立部署脚本，供安装器在平板上部署更新服务。
- `armada-offline-updater.tar.gz`：压缩后的 ARM64 更新器包；运行时由嵌入字节计算其哈希。

部署脚本取自系统仓库的 `system_files/usr/libexec/armada/armada-offline-bootstrap`；更新器包由同一版本系统仓库的 `build_files/build-offline-updater-bundle.py` 从完整 ARM64 系统镜像根目录生成。将这两个文件复制进构建环境。安装器构建只校验它们存在、可读且为普通文件，不下载、不重新生成，也不按文件名猜测版本。

两个参数必须成对给出：只给其中一个时，`build_installer.py` 会在任何下载、工具编译或打包之前报错。脚本与更新包应来自同一次系统构建并保持配对；不匹配的组合会让安装器无法完成部署。`--prepare-only` 可以省略这一对参数，用于只准备工具链、包和固定哈希的流程；此时生成的 props 不含离线更新组件，不能用于单 EXE 发布。

## macOS 版

在 Apple 芯片的 Mac 上安装 .NET SDK 10 和 Xcode 命令行工具（`xcode-select --install`），在仓库根目录运行：

```sh
python3 -B maintenance/build_installer_macos.py --work-dir "$PWD/artifacts/build-macos" --output-dir "$PWD/artifacts/publish-macos"
```

产物为 `artifacts/publish-macos/ArmadaOS Freedom Installer.app`，未签名、未公证，首次打开时需在“系统设置 → 隐私与安全性”中确认。脚本复用 Windows 构建的清单、厂商文件与下载校验：edl-ng 编译为 osx-arm64，打包工具用 `tools/build-boot-packers-macos.sh` 编为通用二进制，libusb 取自同一个已固定哈希的 LibUsbDotNet 包中的 `runtimes/osx-arm64/native/libusb-1.0.dylib`，再按本次产物生成固定哈希，用 `package_edl.py --platform macos-arm64` 制作各型号（TB322FC、TB323FU、TB710FU）9008 工具包（不含 Python）。adb 和 fastboot 取自清单中固定哈希的 Google macOS 版 platform-tools（`platformToolsMacos`），嵌入程序，首次运行时解压到 `~/Library/Application Support/FreedomInstaller/platform-tools`。

## 构建流程

1. 校验仓库内的厂商加载器、驱动及 AOSP 公开测试素材。
2. 从固定官方地址下载平台工具、嵌入式 Python、LibUsbDotNet 官方包、libusb 许可原文和上游 GBL EFI，核对长度与 SHA-256。
3. 编译 `tools/edl-ng` 与 `tools/boot-packers`，生成三个 Windows 工具。
4. 从本次工具产物生成编译时固定哈希，分别制作四代、五代与 TB710FU 的 9008 工具包，并从仓库原始驱动文件制作驱动归档。
5. 生成 MSBuild 输入配置，编译并打包单 EXE。

每次构建会生成 `build/build-record.json`，记录源码文件哈希、上游提交、本次工具哈希和产物路径。提供离线更新组件时，记录还会写入两个文件的绝对路径、字节数、SHA-256 与来源（系统仓库的离线更新包构建脚本）。程序使用编进程序集的本次哈希校验工具，运行时不接受外部配置覆盖。修改工具源码后重新完整构建即可，无需手工替换旧二进制的哈希常量。

## 组件与版本关系

固定版本、URL、长度、SHA-256 和源代码位置见 [maintenance/build-inputs.json](../maintenance/build-inputs.json)。

| 组件 | 来源与处理方式 |
| --- | --- |
| 修改版 edl-ng | 源码在 `tools/edl-ng`，基于 strongtz/edl-ng v1.6.0、提交 `834f91ea2d2dfc7b6337af0e8e54d5ea93e037f4`；包含 Firehose 数据边界处理、Sahara 多镜像加载及 identify 命令。构建脚本为 `tools/build-edl-ng.sh`。 |
| 修改版 boot 打包／解包工具 | 源码在 `tools/boot-packers`，基于 Project-Aloha/DualBootKernelPatcher 提交 `2276163d066596cb0750e76cbfeec5c193b1779f`；包含 PayloadFrom 别名、DTB 对齐和 Windows Unicode 长路径支持。构建脚本为 `tools/build-boot-packers.sh`，inih 源码已包含。 |
| AVB / GPT 脚本 | 直接使用仓库 `maintenance/` 中的四份 Python 源文件。 |
| platform-tools | Google 官方固定版本 37.0.1 Windows ZIP。 |
| Python | python.org 官方 3.14.8 Windows AMD64 嵌入式 ZIP。 |
| libusb DLL | 从 LibUsbDotNet 3.0.167-alpha 官方 NuGet 包提取 Windows x64 文件，与原安装器使用的 DLL 内容一致。 |
| GBL EFI | 从 miner7222/gbl_root_baldur 的 6.2.192-mod2 固定版本取得 PRC / ROW 文件；源码仓库与提交记录在清单中。 |
| 四代、五代、TB710FU 加载器 | 原件分别在 `vendor/qualcomm/g4`、`vendor/qualcomm/g5`、`vendor/qualcomm/tb710fu`，各自独立打包。TB710FU 的加载器取自 ZUXOS 1.5.04.495 免解锁补充包的 `EDL/xbl_s_devprg_ns.melf`。 |
| Qualcomm 驱动 | 2.1.5.2 AMD64 原始文件在 `vendor/qualcomm/driver`，保留签名和 `serial/amd64` 目录结构。 |
| ADB / Fastboot 驱动 | Google USB Driver r13 与 OPLUS 11.0.0000.00000 的 AMD64 原件在 `vendor/android`，保留 INF、CAT 与配套 DLL。构建时按固定哈希校验并压缩嵌入，来源见该目录的 `NOTICE.txt`。 |
| AOSP 测试素材 | 官方公开的 RSA4096 测试文件在 `vendor/aosp`，附官方出处、哈希和上游许可原文。 |

厂商素材保持原始字节，Git 不转换其行尾；特别是五代加载 XML、驱动 INF 与签名目录文件。各开源组件保留自己的许可文件与原作者声明。

## 分步构建与验证

只准备工具、包、固定哈希和 MSBuild 配置：

```sh
python3 -B maintenance/build_installer.py --work-dir /work/build --prepare-only \
  --offline-bootstrap-script /work/offline/armada-offline-bootstrap \
  --offline-updater-bundle /work/offline/armada-offline-updater.tar.gz
```

用于单 EXE 时不要省略这两个参数：生成的 props 会带上它们的路径，后续 `dotnet publish` 按 props 嵌入。只准备工具链和包、不发布单 EXE 时可以省略；此时 props 不含离线更新组件，只适合不生成安装器的流程。

随后可使用该次配置编译：

```sh
dotnet publish src/FreedomInstaller.App/FreedomInstaller.App.csproj -c Release -r win-x64 --self-contained true -p:BundledBuildInputsProps=/work/build/build-inputs.props -o /work/publish
```

只下载或校验官方组件：

```sh
python3 -B maintenance/fetch_build_inputs.py --output-dir /work/downloads
python3 -B maintenance/fetch_build_inputs.py --output-dir /work/downloads --verify-only
```

默认只处理 Windows 构建用到的组件；`--platform macos` 处理 macOS 构建的组件，`--platform all` 处理清单中的全部组件。两个构建脚本各自只下载自己用到的组件。

构建相关 Python 测试：

```sh
python3 -B -m unittest discover -s maintenance/tests -p test_fetch_build_inputs.py
python3 -B -m unittest discover -s maintenance/tests -p test_package_edl.py
python3 -B -m unittest discover -s maintenance/tests -p test_build_installer.py
python3 -B -m unittest discover -s maintenance/tests -p test_android_driver_packaging.py
```

Core 的工具测试需要使用本次 `build-record.json` 中的工具和两个包，并传入同一份 `BundledBuildInputsProps`。涉及真实历史日志的测试仍需其声明的外部输入。编译与合成测试不代替平板安装、签名链和系统启动的真机验收。

Windows 原生打包与解包路径回归（使用本次构建出的两个 EXE；只生成合成测试文件）：

```powershell
python -B tools/boot-packers/tests/windows_paths.py --packer <MultiBootKernelPatcher.exe路径> --unpacker <UnpackTool.exe路径> --work-root <测试目录>
```

覆盖长路径、中文名称、绝对路径和网络共享路径；每个成功用例都会逐字节比较重打包结果。网络共享路径需将测试目录放在实际可用的共享中。失败现场保留在测试目录下。

## Linux 系统更新用 BootTool

BootTool 与安装器共用 Core 中的引导解析、重新打包及签名校验逻辑。`tools/publish-boot-tool.py` 生成包含运行时的 Linux ARM64 工具目录；平板无需安装 .NET SDK 或系统级 .NET 运行时。`tools/boot-tool.Containerfile` 以仓库根目录为构建上下文，产物位于镜像的 `/boot-tool`。`tools/verify-boot-tool.py` 检查文件清单、哈希、架构和启动入口；这项检查不代替设备启动验收。

打包仅取得 `maintenance/build-inputs.json` 中固定的 GBL PRC/ROW 文件，并复用仓库内的 AOSP 公开测试素材。需要离线复用下载内容时，可把清单指定文件名的两个 GBL 文件放进 `tools/boot-tool-inputs/`；它们仍须通过长度和 SHA-256 校验，且被 Git 排除。发布目录保留运行时和第三方许可，不包含 Windows 刷机工具或 SDK。

`boot-tool` 工作流在原生 ARM64 runner 上构建并上传压缩产物，同时在没有 .NET 的运行容器中检查启动。系统仓库从本仓库解析并记录实际源码提交后构建同一工具；系统侧接入前，应先合入所需的 BootTool 源码。
