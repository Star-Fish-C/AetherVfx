using AetherVfx.Core;

static class PreviewSessionTests
{
    public static int Run()
    {
        int count = 0;
        void Test(string name, Action action) { action(); count++; Console.WriteLine("PASS " + name); }
        Test("实验开关不能绕过任何一项兼容门槛，且新实例不继承开关", () =>
        {
            for (int missing = 0; missing < 5; missing++)
            {
                var flags = Enumerable.Range(0, 5).Select(i => i != missing).ToArray();
                var access = new PreviewAccess(new(flags[0], flags[1], flags[2], flags[3], flags[4])) { ExperimentEnabled = true };
                using var session = new PreviewSession();
                Reject(() => session.Begin(access));
                Assert(!session.Active && session.Status.State == PreviewState.Disabled, "兼容门槛可被绕过");
            }
            Assert(!Ready(false).CanStart, "实验开关默认开启");
            Assert(Ready().CanStart && !Ready(false).ExperimentEnabled, "实验开关泄漏到新会话");
        });
        Test("初始化中取消按资源依赖逆序清理，重复停止不会二次释放", () =>
        {
            using var session = new PreviewSession(); var order = new List<string>();
            long ticket = session.Begin(Ready());
            session.Own(ticket, new Lease(() => order.Add("view")));
            session.Own(ticket, new Lease(() => order.Add("mapping")));
            session.Own(ticket, new Lease(() => order.Add("vfx")));
            session.Stop(); session.Stop();
            Assert(order.SequenceEqual(new[] { "vfx", "mapping", "view" }) && !session.Active, "清理顺序错误或重复释放");
            Assert(!session.MarkViewReady(ticket) && !session.MarkVfxCreated(ticket), "取消后旧结果仍生效");
        });
        Test("部分申请后失败释放已有资源并保留错误", () =>
        {
            using var session = new PreviewSession(); int released = 0;
            long ticket = session.Begin(Ready()); session.Own(ticket, new Lease(() => released++));
            session.Fail(ticket, "映射申请失败");
            Assert(released == 1 && session.Status.State == PreviewState.Failed && session.Status.Message == "映射申请失败", "部分初始化清理失败");
            long retry = session.Begin(Ready()); Assert(retry != ticket && session.Active, "已成功清理的失败不能重试");
        });
        Test("连续切换及延迟结果仅影响所属会话，画面始终待验收", () =>
        {
            using var session = new PreviewSession(); int released = 0;
            long previous = 0;
            for (int i = 0; i < 100; i++)
            {
                long ticket = session.Begin(Ready());
                if (i > 0)
                {
                    Assert(!session.Own(previous, new Lease(() => released++)), "过期资源被接管");
                    session.Fail(previous, "旧错误");
                    Assert(session.Status.State == PreviewState.Initializing, "旧错误覆盖新会话");
                }
                session.Own(ticket, new Lease(() => released++));
                session.MarkViewReady(ticket); session.MarkVfxCreated(ticket);
                Assert(session.Status.VisualAcceptance == "PendingGameValidation", "句柄创建被误判为粒子可见");
                previous = ticket;
            }
            session.Stop(); Assert(released == 199, "切换或过期资源泄漏");
        });
        Test("释放某项失败仍清理其他资源，并禁止继续播放", () =>
        {
            using var session = new PreviewSession(); var order = new List<string>();
            long ticket = session.Begin(Ready());
            session.Own(ticket, new Lease(() => order.Add("view")));
            session.Own(ticket, new Lease(() => { order.Add("mapping"); throw new InvalidOperationException("remove failed"); }));
            session.Own(ticket, new Lease(() => order.Add("vfx")));
            session.Stop(); session.Stop();
            Assert(order.SequenceEqual(new[] { "vfx", "mapping", "view" }), "失败中断了其余资源清理");
            Reject(() => session.Begin(Ready()));
            Assert(session.Status.State == PreviewState.Failed && session.Status.Message.Contains("remove failed"), "清理错误丢失");
        });
        Test("过期资源释放失败停止新会话并保留故障", () =>
        {
            using var session = new PreviewSession(); long old = session.Begin(Ready());
            long current = session.Begin(Ready()); int released = 0;
            session.Own(current, new Lease(() => released++));
            session.Own(old, new Lease(() => throw new InvalidOperationException("late release")));
            Assert(released == 1 && !session.Active, "过期释放失败后仍运行");
            Reject(() => session.Begin(Ready()));
        });
        Test("超时与场景终止废弃会话，卸载后迟到资源立即释放", () =>
        {
            var session = new PreviewSession(); long ticket = session.Begin(Ready());
            session.Fail(ticket, "视图初始化超时");
            Assert(!session.MarkViewReady(ticket), "超时后继续初始化");
            ticket = session.Begin(Ready()); session.Stop();
            Assert(!session.MarkVfxCreated(ticket), "场景变化后旧实例仍可创建");
            ticket = session.Begin(Ready()); session.Dispose(); session.Dispose();
            int released = 0; Assert(!session.Own(ticket, new Lease(() => released++)) && released == 1, "卸载后结果泄漏");
            try { session.Begin(Ready()); throw new Exception("卸载后可播放"); } catch (ObjectDisposedException) { }
        });
        Test("视图未就绪不允许宣称播放，启动和清理均失败可再次停止", () =>
        {
            using var session = new PreviewSession(); long ticket = session.Begin(Ready());
            Reject(() => session.MarkVfxCreated(ticket));
            var surface = new BrokenSurface(); var coordinator = new PreviewCoordinator(new Dictionary<PreviewMode, IPreviewSurface> { [PreviewMode.Window] = surface });
            try { coordinator.Play(PreviewMode.Window, PreviewScope.Asset, new("asset", "test", null!)); throw new Exception("未抛错"); }
            catch (AggregateException ex) { Assert(ex.InnerExceptions.Count == 2 && coordinator.Mode == PreviewMode.Window, "丢失启动或清理错误"); }
            surface.FailStop = false; coordinator.Stop(); Assert(coordinator.Mode is null && !surface.Active, "无法重试停止");
        });
        return count;
    }
    private static PreviewAccess Ready(bool enabled = true) => new(new(true, true, true, true, true)) { ExperimentEnabled = enabled };
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new Exception("应拒绝操作");
    }
    private sealed class Lease(Action release) : IDisposable { public void Dispose() => release(); }
    private sealed class BrokenSurface : IPreviewSurface
    {
        public bool FailStop = true;
        public bool Active { get; private set; }
        public PreviewStatus Snapshot => new(PreviewState.Failed, "test");
        public void Play(PreviewPayload _) { Active = true; throw new InvalidOperationException("start failed"); }
        public void Stop() { if (FailStop) throw new InvalidOperationException("stop failed"); Active = false; }
    }
}
