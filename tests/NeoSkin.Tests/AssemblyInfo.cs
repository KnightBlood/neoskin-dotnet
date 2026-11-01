using Xunit;

// 组合根里有少量进程级静态状态（PageView 的通知/公告提供者，对应 Java 版
// 的静态 InboxProvider）。xUnit 默认并行执行测试类会让不同测试的 App 互相
// 覆盖这些提供者，因此这里串行执行。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
