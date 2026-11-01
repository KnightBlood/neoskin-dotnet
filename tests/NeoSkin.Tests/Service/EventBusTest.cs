using NeoSkin.Web.Event;

namespace NeoSkin.Tests.Service;

/// <summary><see cref="EventDispatcher"/> 的注册/派发/顺序/隔离测试。</summary>
public sealed class EventBusTest
{
    [Fact]
    public void DispatchesInRegistrationOrder()
    {
        var dispatcher = new EventDispatcher();
        var calls = new List<string>();

        dispatcher.Listen<Events.RenderingHeader>(_ => calls.Add("first"));
        dispatcher.Listen<Events.RenderingHeader>(_ => calls.Add("second"));

        dispatcher.Dispatch(new Events.RenderingHeader([]));

        Assert.Equal(["first", "second"], calls);
        Assert.Equal(2, dispatcher.Count(typeof(Events.RenderingHeader)));
    }

    [Fact]
    public void ListenersCanMutateEventCollections()
    {
        var dispatcher = new EventDispatcher();
        dispatcher.Listen<Events.RenderingHeader>(e => e.AddContent("<meta a>"));
        dispatcher.Listen<Events.RenderingFooter>(e => e.AddContent("<script b>"));

        var header = new List<string>();
        var footer = new List<string>();
        dispatcher.Dispatch(new Events.RenderingHeader(header));
        dispatcher.Dispatch(new Events.RenderingFooter(footer));

        Assert.Equal(["<meta a>"], header);
        Assert.Equal(["<script b>"], footer);
    }

    [Fact]
    public void OnlyMatchingClassListenersRun()
    {
        var dispatcher = new EventDispatcher();
        int headerCalls = 0;
        dispatcher.Listen<Events.RenderingHeader>(_ => headerCalls++);

        dispatcher.Dispatch(new Events.RenderingFooter([]));
        Assert.Equal(0, headerCalls);
    }

    [Fact]
    public void StringEventsCarryPayload()
    {
        var dispatcher = new EventDispatcher();
        string? seen = null;
        int payload = 0;

        dispatcher.Listen("texture.deleted", args =>
        {
            seen = (string?)args[0];
            payload = (int)args[1]!;
        });

        dispatcher.Dispatch("texture.deleted", "hash", 42);

        Assert.Equal("hash", seen);
        Assert.Equal(42, payload);
        Assert.Equal(1, dispatcher.Count("texture.deleted"));
    }

    [Fact]
    public void ListenAllSeesEveryEvent()
    {
        var dispatcher = new EventDispatcher();
        var seen = new List<Type>();
        dispatcher.ListenAll(e => seen.Add(e.GetType()));

        dispatcher.Dispatch(new Events.RenderingHeader([]));
        dispatcher.Dispatch(new Events.PlayerWillBeAdded("Steve"));

        Assert.Equal([typeof(Events.RenderingHeader), typeof(Events.PlayerWillBeAdded)], seen);
    }

    [Fact]
    public void ListenerExceptionsPropagate()
    {
        var dispatcher = new EventDispatcher();
        dispatcher.Listen<Events.RenderingHeader>(_ => throw new InvalidOperationException("boom"));

        Assert.Throws<InvalidOperationException>(() =>
            dispatcher.Dispatch(new Events.RenderingHeader([])));
    }

    [Fact]
    public void ClearRemovesEverything()
    {
        var dispatcher = new EventDispatcher();
        dispatcher.Listen<Events.RenderingHeader>(_ => { });
        dispatcher.Listen("x", _ => { });
        dispatcher.Clear();

        Assert.Equal(0, dispatcher.Count(typeof(Events.RenderingHeader)));
        Assert.Equal(0, dispatcher.Count("x"));
    }

    [Fact]
    public void RuntimeCanBeReplaced()
    {
        EventRuntime.Set(new EventDispatcher());
        int calls = 0;
        EventRuntime.Dispatcher.Listen<Events.UserTryToLogin>(_ => calls++);

        EventRuntime.Fire(new Events.UserTryToLogin("a@b.c", "web"));
        Assert.Equal(1, calls);

        EventRuntime.Set(null);
        Assert.Equal(0, EventRuntime.Dispatcher.Count(typeof(Events.UserTryToLogin)));
    }
}
