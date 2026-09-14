# E-10 Docker compose 与部署脚本完善

## 状态
- 列：Backlog
- Owner: AI Agent
- 复杂度：S
- 依赖：无

## 目标
补齐 PRD 5.4 可部署性尾巴：现仅根目录 Dockerfile（--gate 模式）。增加 docker-compose 样例
（双节点 cluster + gate 拓扑）、`.dockerignore` 校验、部署脚本（build/push 镜像）。

## 子任务
- [ ] `docker-compose.yml`：node1/node2/gate 三容器组网演示（静态 registry 配置）。
- [ ] `scripts/docker-build.sh`（可选 push 参数）。
- [ ] CI/release 工作流增加可选镜像构建 job。
- [ ] 文档：docs/release-guide.md 增补。

## 验收标准
- `docker compose up` 后两节点互 discover，gate 可连接并转发到集群内 actor。

## 测试用例
| # | 用例 | 预期 |
|---|------|------|
| 1 | compose up 三容器 | 日志显示节点互注册 |
| 2 | 客户端连 gate 发请求 | 收到集群 actor 响应 |

## 相关文件
- `Dockerfile`
- `scripts/publish-packages.sh`
- `docs/release-guide.md`
