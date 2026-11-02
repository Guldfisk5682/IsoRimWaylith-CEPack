using System;
using CombatExtended;
using Verse;

namespace IsoRimCEPackZ
{
    // Explicit map/operator overlay for an unspawned query subject. Shared readers do not own the lease.
    // The caller restores the previous state in finally; nested queries preserve their caller's overlay.
    internal static class QueryContext
    {
        internal readonly record struct State(Thing Subject, Map Map, Pawn Operator);
        [ThreadStatic] private static State current;

        internal static State Enter(Thing subject, Map map, Pawn @operator)
        {
            State before = current;
            current = new(subject, map, @operator);
            return before;
        }

        internal static void Exit(State before) => current = before;

        internal static Map MapOf(Thing thing) => thing != null && thing == current.Subject ? current.Map : thing?.Map;
        internal static Map MapHeldOf(Thing thing) => thing != null && thing == current.Subject ? current.Map : thing?.MapHeld;
        internal static Pawn OperatorOf(Thing thing) => thing != null && thing == current.Subject
            ? current.Operator : CE_Utility.TryGetTurretOperator(thing);
    }
}
