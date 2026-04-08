# 安全与认证边界：Orleans 到底自己做了什么，又把什么留给宿主和外围设施（第二十二篇）

这一篇只讲安全这条线。

先把结论说直白一点：

- Orleans 自己真正做的安全能力，主要是连接层的 TLS、证书校验、应用协议协商和握手时的基本身份确认。
- Orleans 没有把“应用级鉴权”“消息签名”“权限模型”做成框架内核。
- 消息层本身并没有再加一层框架级加密或认证，消息加密与链路保密基本都靠 TLS。
- `ClusterId`、`NodeIdentity`、`SiloAddress` 这些更像连接和集群身份，不是用户身份。
- 证书加载、TLS 选项、是否启用双向认证、是否校验证书链，这些都在 `Orleans.Connections.Security` 里；至于证书从哪来、怎么发、怎么轮转，还是宿主和外围设施自己管。

如果把这篇压成一句话，就是：

> Orleans 只把“安全连接”这件事做成了框架能力，但没有把“谁能做什么”做成框架能力。

这也是它这块看起来比较薄的原因。

---

## 1. 先把整条主链压成一张图

```text
宿主配置
  -> UseTls(...)
  -> TlsOptions
  -> CertificateLoader / 证书选择器
  -> UseServerTls / UseClientTls

连接建立
  -> TlsServerConnectionMiddleware / TlsClientConnectionMiddleware
  -> SslStream.AuthenticateAsServerAsync / AuthenticateAsClientAsync
  -> 证书链、EKU、私钥、revocation、远程证书验证
  -> ITlsConnectionFeature / ITlsApplicationProtocolFeature / ITlsHandshakeFeature
  -> Orleans1 应用协议协商

握手后
  -> ConnectionPreambleHelper
  -> ConnectionPreamble { NetworkProtocolVersion, NodeIdentity, SiloAddress, ClusterId }
  -> ClientOutboundConnection / SiloConnection / GatewayInboundConnection
  -> NodeIdentity 和 ClusterId 校验
  -> MessageCenter 开始收消息

消息层
  -> 普通 Message / Response / RejectionResponse
  -> 没有额外的消息签名或加密层
  -> 具体安全边界仍然取决于底下那条 TLS 连接
```

这张图里最关键的一点是：

Orleans 的安全并不是“消息先过权限检查，再进 runtime”。

它是“先把连接安全地拉起来，再在连接里用 preamble 和 cluster id 认一下身份，然后就进入正常消息流”。

---

## 2. `Orleans.Connections.Security` 这一层到底负责什么

关键文件：

- `src/Orleans.Connections.Security/Hosting/HostingExtensions.IClientBuilder.cs`
- `src/Orleans.Connections.Security/Hosting/HostingExtensions.ISiloBuilder.cs`
- `src/Orleans.Connections.Security/Security/TlsOptions.cs`
- `src/Orleans.Connections.Security/Security/TlsServerConnectionMiddleware.cs`
- `src/Orleans.Connections.Security/Security/TlsClientConnectionMiddleware.cs`
- `src/Orleans.Connections.Security/Security/CertificateLoader.cs`

### 2.1 `UseTls(...)` 是入口，不是默认行为

Orleans 的 TLS 不是默认自动开的。

你得显式在 builder 上调用：

- `UseTls(...)` on `ISiloBuilder`
- `UseTls(...)` on `IClientBuilder`

这两个入口最后都会把配置翻成 `TlsOptions`，再挂到连接配置上。

这说明 Orleans 对安全的态度很直接：

它提供接线口，但不替你决定系统是否必须走 TLS。

### 2.2 `TlsOptions` 是这条线的总配置

`TlsOptions` 里最关键的是这些项：

- `LocalCertificate`
- `LocalServerCertificateSelector`
- `LocalClientCertificateSelector`
- `RemoteCertificateMode`
- `ClientCertificateMode`
- `RemoteCertificateValidation`
- `SslProtocols`
- `CheckCertificateRevocation`
- `HandshakeTimeout`
- `OnAuthenticateAsServer`
- `OnAuthenticateAsClient`

这里面能看出来几件事：

1. Orleans 关心的是连接认证，不是业务授权。
2. 它把“证书从哪来”留给宿主。
3. 它把“对端证书要不要强制”留给配置。
4. 它允许你在认证前后再插一层定制逻辑，但那也是连接级逻辑。

### 2.3 `CertificateLoader` 只做证书筛选，不做证书治理

`CertificateLoader.LoadFromStoreCert(...)` 会从证书库里找 subject 匹配的证书，再根据用途筛：

- server 证书要有 Server Authentication EKU
- client 证书要有 Client Authentication EKU
- 还要有可访问的私钥

它做的是“拿到一个可用证书”。

它不做的是：

- 证书颁发
- 证书轮转
- 证书吊销策略设计
- 组织级的证书分发

这些都不在 Orleans 里。

### 2.4 `TlsServerConnectionMiddleware` 和 `TlsClientConnectionMiddleware` 真正做认证

这两个 middleware 做的事很像：

1. 包一层 `SslStream`
2. 发起握手
3. 校验远端证书
4. 填入连接特性
5. 认证成功后把原始 transport 换成 TLS transport

服务端侧更重一点，因为它会处理：

- server certificate 选择
- 是否要求客户端证书
- `RemoteCertificateMode`
- `RemoteCertificateValidation`
- revocation check
- SNI

客户端侧则是：

- 本地 client certificate 选择
- 远端证书校验
- 是否接受无证书服务端

这就是 Orleans 连接级安全的核心实现。

---

## 3. 连接认证和身份确认，其实是两层东西

关键文件：

- `src/Orleans.Core/Networking/ConnectionPreamble.cs`
- `src/Orleans.Core/Networking/ClientOutboundConnection.cs`
- `src/Orleans.Runtime/Networking/SiloConnection.cs`
- `src/Orleans.Runtime/Networking/GatewayInboundConnection.cs`
- `src/Orleans.Connections.Security/Security/OrleansApplicationProtocol.cs`

### 3.1 TLS 先解决“这条线是不是安全的”

TLS 主要解决的是：

- 链路加密
- 对端证书验证
- 双向证书认证
- 协议版本协商

也就是说，TLS 解决的是“连接安全”。

### 3.2 `ConnectionPreamble` 再解决“你是谁”

TLS 通过之后，Orleans 还会继续写一段 preamble：

- `NetworkProtocolVersion`
- `NodeIdentity`
- `SiloAddress`
- `ClusterId`

这段 preamble 才是 Orleans 自己的连接身份协议。

它很重要，因为 TLS 只告诉你“对端证书看起来合法吗”，但不会替你解释：

- 这个连接属于哪个 cluster
- 这个连接是 client、silo 还是 gateway
- 这个连接对应哪个节点身份

这些都得靠 Orleans 自己的 preamble 再确认一次。

### 3.3 Client 和 silo 的 preamble 语义不一样

`ClientOutboundConnection` 写出去的 preamble 里：

- `NodeIdentity` 是 `ClientId.GrainId`
- `SiloAddress` 为空
- `ClusterId` 是当前 client 的 cluster id

`SiloConnection` 和 `GatewayInboundConnection` 则会用：

- `NodeIdentity = Constants.SiloDirectConnectionId`
- `SiloAddress = 本机 silo address`
- `ClusterId = 本机 cluster id`

然后双方都会做检查：

- `ClusterId` 必须对得上
- `NodeIdentity` 必须符合预期的连接类型

这里的意思很直接：

TLS 认的是证书身份，preamble 认的是 Orleans 集群语义身份。

两者不是一回事。

### 3.4 `Orleans1` 是连接级应用协议，不是业务鉴权

`OrleansApplicationProtocol.Orleans1` 只是一个 ALPN 常量。

它的作用是告诉 TLS 握手：

“这条连接跑的是 Orleans 的协议栈。”

它不是用户认证，也不是权限授权。

---

## 4. 消息层为什么这么薄

关键文件：

- `src/Orleans.Core/Messaging/ClientMessageCenter.cs`
- `src/Orleans.Core/Messaging/MessageSerializer.cs`
- `src/Orleans.Runtime/Networking/GatewayInboundConnection.cs`
- `src/Orleans.Runtime/Networking/SiloConnection.cs`

### 4.1 Orleans 的消息层没有单独再包一层安全协议

从 `MessageSerializer` 到 `MessageCenter`，你看到的是：

- 消息头
- body
- 路由
- 回调
- rejection

你看不到的是：

- 消息签名
- 每条消息的单独加密
- 细粒度权限检查
- 框架内置的 token 验证

这说明 Orleans 的安全边界基本停在 transport 上。

### 4.2 这不是漏掉了，而是设计选择

如果每条消息都再做一层加密和鉴权，代价会很大，而且会把 Orleans 已经很重的消息路径再压一层。

所以它选择的是：

- 连接层做 TLS
- 集群层做 preamble 和 cluster id 校验
- 业务层的权限、身份、租户隔离留给宿主或外围设施

这也是为什么 Orleans 这块看起来“薄”。

它不是没想到，而是没往消息级安全那边继续长。

### 4.3 `GatewayInboundConnection` 和 `SiloConnection` 只做集群级检查

这两个入口都会检查：

- 连接过来的是不是预期类型
- `ClusterId` 对不对
- `NodeIdentity` 对不对

但它们不会做更细的“谁有权限调用哪个 grain method”。

也就是说，它们只确认：

- 这是不是我们集群里的合法连接
- 这条消息能不能继续进 runtime

不会确认：

- 这个 client 是否允许访问某个 grain
- 这个请求是否属于某个 tenant
- 这个调用是否符合业务权限

---

## 5. 哪些是 Orleans 自己做的，哪些不是

这部分最好单独分开看。

### 5.1 Orleans 自己做的

- TLS 连接的接入
- server/client 证书选择
- 证书 EKU 检查
- 私钥检查
- 对端证书验证
- handshake timeout
- ALPN 协议协商
- `ConnectionPreamble` 的集群和节点身份确认
- connection feature 暴露出证书和握手结果

### 5.2 Orleans 没自己做的

- 用户身份认证
- 细粒度权限控制
- 业务级授权
- 令牌校验
- 消息签名
- 租户隔离
- KMS/HSM/证书生命周期管理
- 零信任网络策略

这些都得靠宿主、网关、反向代理、Kubernetes、云负载均衡、API 网关，或者你自己业务层再补。

### 5.3 这就是它相对薄的原因

我觉得这不是 Orleans 没能力，而是它把边界画得很清楚：

它是一个分布式运行时，不是一个完整 IAM 平台。

所以它只管“连接安全”和“集群身份一致性”，不管“用户到底有没有权限”。

这条线如果再往下长，系统会很快变重，而且会跟宿主环境深度耦合。

---

## 6. 这块里我觉得不够完整的地方

### 6.1 安全能力是模块化的，但不够统一

TLS 在 `Orleans.Connections.Security`，连接身份在 `ConnectionPreamble`，集群身份在 `ClusterOptions` 和 `LocalSiloDetails`，消息层又是另一套。

这能工作，但你第一次看源码的时候，会感觉像在拼几张不完全重叠的图。

### 6.2 没有框架级的应用鉴权

这其实是最大的空白。

如果你想让 Orleans 自己回答：

“这个 caller 能不能调这个 grain method？”

源码里并没有一个统一入口。

你得自己在外围做，或者在应用层过滤器里做。

### 6.3 证书只是被加载和校验，不是被治理

`CertificateLoader` 解决的是“可用”，不是“运维”。

也就是说，你如果想做：

- 证书自动轮转
- 多租户证书隔离
- 多环境证书发布

Orleans 这边都不会替你兜住。

### 6.4 连通性校验比身份授权更强

Orleans 会很认真地检查：

- cluster id
- connection type
- handshake 是否通过
- 证书是否合法

但它不对“谁能调用谁”给出同等强度的答案。

这就是它安全边界比较薄、但也比较干净的地方。

---

## 7. 如果要自己复刻，我会怎么拆

如果你要复刻一版更清楚的安全层，我会把它拆成三块。

### 7.1 传输安全层

只负责：

- TLS
- 证书
- 加密
- 协议协商

### 7.2 集群身份层

只负责：

- cluster id
- node identity
- connection preamble
- 节点类型确认

### 7.3 应用安全层

只负责：

- caller 身份
- grain method 授权
- tenant
- policy
- auditing

这样做的好处是：

- 连接安全和业务安全不会混在一起
- 框架职责更清楚
- 以后接网关、零信任、审计系统也更容易

Orleans 现在主要停在前两层，第三层基本留给外部。

---

## 8. 这一篇最该反复看的源码文件

建议按这个顺序读：

1. `src/Orleans.Connections.Security/Hosting/HostingExtensions.IClientBuilder.cs`
2. `src/Orleans.Connections.Security/Hosting/HostingExtensions.ISiloBuilder.cs`
3. `src/Orleans.Connections.Security/Security/TlsOptions.cs`
4. `src/Orleans.Connections.Security/Security/TlsServerConnectionMiddleware.cs`
5. `src/Orleans.Connections.Security/Security/TlsClientConnectionMiddleware.cs`
6. `src/Orleans.Connections.Security/Security/CertificateLoader.cs`
7. `src/Orleans.Core/Networking/ConnectionPreamble.cs`
8. `src/Orleans.Core/Networking/ClientOutboundConnection.cs`
9. `src/Orleans.Runtime/Networking/SiloConnection.cs`
10. `src/Orleans.Runtime/Networking/GatewayInboundConnection.cs`
11. `src/Orleans.Connections.Security/Security/OrleansApplicationProtocol.cs`

如果把这几份源码顺下来，你会很清楚地看到：

- Orleans 真的做了 TLS
- Orleans 真的做了证书验证
- Orleans 真的做了集群握手身份确认
- 但 Orleans 没有把应用授权做成框架标准件

这也是这块“薄”的根本原因。

