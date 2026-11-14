# 测试

## 公共维护单元测试

`maintenance/tests` 下所有 `test_*.py` 都是公共、离线的合成夹具测试，可用一个命令运行：

```sh
python3 -B maintenance/run_unit_tests.py
```

运行器自动发现全部普通测试模块，新增模块无需登记；任何断言失败都会使命令以非零状态退出。CI 的 Linux 作业运行同一条命令，名称是 “public fixture maintenance checks”。

这条命令只覆盖公共维护夹具。Core 的包契约与相关行为测试由 `package-contracts` 工作流用 `dotnet test` 的过滤器运行，不在这里重复；两者都不代表 Core 或私有夹具的全量覆盖。

## 特权引导生命周期 harness

`maintenance/tests/test_bootstrap_lifecycle.py` 不是普通单元测试，也不属于上述通用 CI。它在一次性容器内执行真实的 BusyBox shell 夹具，需要：

- `TF_BOOTSTRAP_CONTAINER_TEST=1` 且处于容器环境；
- C# 的 `TfBootstrapScriptTests` 在设置 `TF_BOOTSTRAP_FIXTURE_EXPORT_DIR` 时导出的 `launcher.sh` 与 `cleanup.sh`；
- `TF_BOOTSTRAP_REAL_BUSYBOX` 指向真实 BusyBox 可执行文件；
- `TF_BOOTSTRAP_TEST_WORK` 指向 `/evidence` 下本次运行专属的新目录；
- 挂载用例还需要 `CAP_SYS_ADMIN`，缺少时报告 BLOCKED，绝不伪装通过。

这些前置条件不满足时 harness 会主动拒绝运行，因此不要把它接入通用作业，也不要在主机 runner 上直接运行。普通测试运行器会打印它的排除原因和上述要求；需要时按真实前置条件单独调用。
