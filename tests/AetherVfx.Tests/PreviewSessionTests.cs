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
        Test("释放失败时保留失败资源及其依赖，禁止隐式重试和继续播放", () =>
        {
            var session = new PreviewSession(); var order = new List<string>();
            long ticket = session.Begin(Ready());
            session.Own(ticket, new Lease(() => order.Add("view")));
            session.Own(ticket, new Lease(() => { order.Add("mapping"); throw new InvalidOperationException("remove failed"); }));
            session.Own(ticket, new Lease(() => order.Add("vfx")));
            session.Stop(); session.Stop();
            Assert(order.SequenceEqual(new[] { "vfx", "mapping" }), "失败资源或它的依赖被重复释放");
            Reject(() => session.Begin(Ready()));
            Assert(session.Status.State == PreviewState.Failed && session.Status.Message.Contains("remove failed"), "清理错误丢失");
            Reject(session.RetryFailedCleanup);
            Reject(session.Dispose);
        });
        Test("过期资源释放失败停止新会话并保留故障", () =>
        {
            var session = new PreviewSession(); long old = session.Begin(Ready());
            long current = session.Begin(Ready()); int released = 0;
            session.Own(current, new Lease(() => released++));
            session.Own(old, new Lease(() => throw new InvalidOperationException("late release")));
            Assert(released == 0 && !session.Active, "过期释放失败时未保留当前资源");
            Reject(() => session.Begin(Ready()));
            Reject(session.Dispose);
        });
        Test("仅明确承诺安全重试的租约可重新清理，成功后按顺序释放依赖", () =>
        {
            using var session = new PreviewSession(); var order = new List<string>(); int attempts = 0;
            long ticket = session.Begin(Ready());
            session.Own(ticket, new Lease(() => order.Add("view")));
            session.Own(ticket, new RetryableLease(() =>
            {
                order.Add("vfx");
                if (++attempts == 1) throw new InvalidOperationException("temporarily unavailable");
            }));
            session.Stop(); session.Stop();
            Assert(order.SequenceEqual(new[] { "vfx" }), "隐式重试或提前释放依赖");
            session.RetryFailedCleanup();
            Assert(order.SequenceEqual(new[] { "vfx", "vfx", "view" }), "重试后清理顺序错误");
            Assert(session.Status.State == PreviewState.Idle && session.Begin(Ready()) != ticket, "成功清理后未恢复");
        });
        Test("多个失败租约不会在恢复上层时隐式重试下层", () =>
        {
            var session = new PreviewSession(); var order = new List<string>();
            long ticket = session.Begin(Ready());
            session.Own(ticket, new Lease(() => order.Add("view")));
            session.Own(ticket, new Lease(() => { order.Add("mapping"); throw new InvalidOperationException("mapping failure"); }));
            session.Stop();
            int lateAttempts = 0;
            session.Own(ticket, new RetryableLease(() =>
            {
                order.Add("late");
                if (++lateAttempts == 1) throw new InvalidOperationException("late failure");
            }));
            session.RetryFailedCleanup();
            Assert(order.SequenceEqual(new[] { "mapping", "late", "late" }), "重试上层时错误清理了下层");
            Assert(session.Status.State == PreviewState.Failed, "下层错误被清除");
            Reject(session.RetryFailedCleanup);
            Reject(session.Dispose);
        });
        Test("卸载清理失败保留会话，安全重试成功后才能完成卸载", () =>
        {
            var session = new PreviewSession(); int attempts = 0, viewReleased = 0;
            long ticket = session.Begin(Ready());
            session.Own(ticket, new Lease(() => viewReleased++));
            session.Own(ticket, new RetryableLease(() =>
            {
                if (++attempts == 1) throw new InvalidOperationException("cleanup pending");
            }));
            Reject(session.Dispose);
            Reject(() => session.Begin(Ready()));
            Assert(attempts == 1 && viewReleased == 0, "卸载失败时提前释放依赖");
            session.RetryFailedCleanup(); session.Dispose();
            Assert(attempts == 2 && viewReleased == 1, "重试卸载后资源未释放");
            try { session.Begin(Ready()); throw new Exception("卸载后可播放"); } catch (ObjectDisposedException) { }
        });
        Test("卸载后迟到资源释放失败时仍允许明确安全重试", () =>
        {
            var session = new PreviewSession(); int attempts = 0;
            long ticket = session.Begin(Ready()); session.Dispose();
            Assert(!session.Own(ticket, new RetryableLease(() =>
            {
                if (++attempts == 1) throw new InvalidOperationException("late cleanup pending");
            })), "卸载后接管了资源");
            session.RetryFailedCleanup(); session.Dispose();
            Assert(attempts == 2, "迟到资源未安全重试");
            try { session.Begin(Ready()); throw new Exception("卸载后可播放"); } catch (ObjectDisposedException) { }
        });
        Test("协调器在资源清理失败时保留预览身份，恢复后才能清除", () =>
        {
            var surface = new SessionSurface();
            var coordinator = new PreviewCoordinator(new Dictionary<PreviewMode, IPreviewSurface> { [PreviewMode.Window] = surface });
            coordinator.Play(PreviewMode.Window, PreviewScope.Asset, new("asset", "test", null!));
            Reject(coordinator.Stop);
            Assert(coordinator.Mode == PreviewMode.Window && coordinator.Active && surface.ViewReleased == 0, "协调器丢失仍被持有的资源");
            surface.Session.RetryFailedCleanup(); coordinator.Stop();
            Assert(coordinator.Mode is null && surface.ViewReleased == 1, "清理完成后未恢复停止状态");
            surface.Session.Dispose();
        });
        Test("清理回调中迟到资源失败不会误删租约或释放依赖", () =>
        {
            using var session = new PreviewSession(); var order = new List<string>(); int attempts = 0;
            long ticket = session.Begin(Ready());
            session.Own(ticket, new Lease(() => order.Add("view")));
            session.Own(ticket, new Lease(() =>
            {
                order.Add("vfx");
                Reject(() => session.Begin(Ready()));
                Reject(session.Dispose);
                session.Own(ticket, new RetryableLease(() =>
                {
                    order.Add("late");
                    if (++attempts == 1) throw new InvalidOperationException("late failure");
                }));
            }));
            session.Stop(); session.Stop();
            Assert(order.SequenceEqual(new[] { "vfx", "late" }) && session.Status.State == PreviewState.Failed, "重入清理丢失错误或提前释放依赖");
            session.RetryFailedCleanup();
            Assert(order.SequenceEqual(new[] { "vfx", "late", "late", "view" }), "重试错误释放已完成的租约");
        });
        Test("重复或过期提交已持有的同一租约不会导致提前释放", () =>
        {
            using var session = new PreviewSession(); int released = 0;
            long old = session.Begin(Ready()); long current = session.Begin(Ready());
            var lease = new Lease(() => released++);
            session.Own(current, lease);
            Reject(() => session.Own(current, lease));
            Reject(() => session.Own(old, lease));
            Assert(released == 0 && session.Active, "重复提交导致活动资源被释放");
            session.Stop(); Assert(released == 1, "原有租约未正确释放");
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
    private sealed class RetryableLease(Action release) : IRetryablePreviewLease { public void Dispose() => release(); }
    private sealed class SessionSurface : IPreviewSurface
    {
        public readonly PreviewSession Session = new();
        public int ViewReleased;
        private int attempts;
        public bool Active => Session.Active || Session.CleanupPending;
        public PreviewStatus Snapshot => Session.Status;
        public void Play(PreviewPayload _)
        {
            long ticket = Session.Begin(Ready());
            Session.Own(ticket, new Lease(() => ViewReleased++));
            Session.Own(ticket, new RetryableLease(() =>
            {
                if (++attempts == 1) throw new InvalidOperationException("cleanup pending");
            }));
            Session.MarkViewReady(ticket); Session.MarkVfxCreated(ticket);
        }
        public void Stop()
        {
            Session.Stop();
            if (Session.CleanupPending) throw new InvalidOperationException(Session.Status.Message);
        }
    }
    private sealed class BrokenSurface : IPreviewSurface
    {
        public bool FailStop = true;
        public bool Active { get; private set; }
        public PreviewStatus Snapshot => new(PreviewState.Failed, "test");
        public void Play(PreviewPayload _) { Active = true; throw new InvalidOperationException("start failed"); }
        public void Stop() { if (FailStop) throw new InvalidOperationException("stop failed"); Active = false; }
    }
}
