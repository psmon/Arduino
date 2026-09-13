using Akka.Actor;

namespace AskBot.Host;

/// <summary>
/// AOT-safe actor creation.
///
/// Every <c>Props.Create&lt;T&gt;()</c> overload in Akka 1.6 - including the
/// <c>Props.Create(() =&gt; new T())</c> lambda form, which is decomposed into a
/// <c>NewExpression</c> and its argument list - ends up at
/// <c>ActivatorProducer</c>, i.e. <c>Activator.CreateInstance</c>. Under Native
/// AOT the trimmer has no reason to keep a constructor nobody calls directly, so
/// the published binary fails at actor creation time with:
///
///   MissingMethodException: No parameterless constructor defined for type '...'
///
/// <c>Props.CreateBy(IIndirectActorProducer)</c> is the one path with no
/// reflection in it: the producer below closes over a real <c>new</c>, which the
/// compiler emits as a direct call and the trimmer therefore keeps.
/// </summary>
public static class AotProps
{
    public static Props Of<TActor>(Func<TActor> factory) where TActor : ActorBase
        => Props.CreateBy(new FuncProducer<TActor>(factory));

    private sealed class FuncProducer<TActor>(Func<TActor> factory) : IIndirectActorProducer
        where TActor : ActorBase
    {
        public Type ActorType => typeof(TActor);
        public ActorBase Produce() => factory();
        public void Release(ActorBase actor) { }
    }
}
