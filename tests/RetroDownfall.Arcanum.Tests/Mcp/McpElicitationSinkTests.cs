using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class McpElicitationSinkTests
{

    [Fact]
    public void Resolve_with_no_caller_declines_with_the_no_attended_turn_reason()
    {

        McpElicitationSink sink = new();

        Assert.False(sink.TryResolve(out IHumanPromptLiveEmitter? emitter, out string? reason));

        Assert.Null(emitter);

        Assert.Equal(McpElicitationSink.NoAttendedTurnReason, reason);

    }

    [Fact]
    public void Resolve_with_one_caller_returns_that_emitter()
    {

        McpElicitationSink sink = new();

        RecordingEmitter caller = new();

        using IDisposable scope = sink.Enter(caller);

        Assert.True(sink.TryResolve(out IHumanPromptLiveEmitter? emitter, out string? reason));

        Assert.Same(caller, emitter);

        Assert.Null(reason);

    }

    [Fact]
    public void Resolve_with_the_same_emitter_entered_twice_stays_unambiguous()
    {

        McpElicitationSink sink = new();

        RecordingEmitter caller = new();

        using IDisposable first = sink.Enter(caller);

        using IDisposable second = sink.Enter(caller);

        Assert.True(sink.TryResolve(out IHumanPromptLiveEmitter? emitter, out _));

        Assert.Same(caller, emitter);

    }

    [Fact]
    public void Resolve_with_two_different_emitters_declines_as_ambiguous()
    {

        McpElicitationSink sink = new();

        using IDisposable first = sink.Enter(new RecordingEmitter());

        using IDisposable second = sink.Enter(new RecordingEmitter());

        Assert.False(sink.TryResolve(out IHumanPromptLiveEmitter? emitter, out string? reason));

        Assert.Null(emitter);

        Assert.Equal(McpElicitationSink.AmbiguousTurnReason, reason);

    }

    [Fact]
    public void Entering_a_null_emitter_registers_nothing()
    {

        McpElicitationSink sink = new();

        using IDisposable scope = sink.Enter(null);

        Assert.False(sink.TryResolve(out _, out string? reason));

        Assert.Equal(McpElicitationSink.NoAttendedTurnReason, reason);

    }

    [Fact]
    public void Disposing_a_scope_removes_only_its_own_registration_and_is_idempotent()
    {

        McpElicitationSink sink = new();

        RecordingEmitter surviving = new();

        IDisposable survivingScope = sink.Enter(surviving);

        IDisposable departingScope = sink.Enter(new RecordingEmitter());

        Assert.False(sink.TryResolve(out _, out _));

        departingScope.Dispose();

        departingScope.Dispose();

        Assert.True(sink.TryResolve(out IHumanPromptLiveEmitter? emitter, out _));

        Assert.Same(surviving, emitter);

        survivingScope.Dispose();

        Assert.False(sink.TryResolve(out _, out string? reason));

        Assert.Equal(McpElicitationSink.NoAttendedTurnReason, reason);

    }

    private sealed class RecordingEmitter : IHumanPromptLiveEmitter
    {

        public ValueTask EmitAsync(IntelligenceEvent evt, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    }

}
