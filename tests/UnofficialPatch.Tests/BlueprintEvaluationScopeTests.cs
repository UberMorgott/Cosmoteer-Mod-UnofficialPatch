using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Halfling.Application;
using HarmonyLib;
using Xunit;

namespace UnofficialPatch.Tests;

public class BlueprintEvaluationScopeTests
{
    [Fact]
    public void ScopeMatchesInstalledUpdateAndRejectsChangedBody()
    {
        var native = PatchProcessor.GetOriginalInstructions(BlueprintEvaluationScope.Target);
        Assert.Equal(native.Count, BlueprintEvaluationScope.Transpiler(native).Count());
        native.Insert(0, new CodeInstruction(OpCodes.Nop));
        Assert.Throws<InvalidOperationException>(() => BlueprintEvaluationScope.Transpiler(native).ToList());
    }

    [Fact]
    public void ActualNativeUpdateExceptionClosesIntervalWithoutReplacingException()
    {
        const string owner = "performance.tests.blueprint-update-scope";
        var harmony = new Harmony(owner);
        object director = RuntimeHelpers.GetUninitializedObject(typeof(Director));
        // The first native instruction accesses the uninitialized update counter. No window or game is created.
        Exception native = Assert.Throws<TargetInvocationException>(() => BlueprintEvaluationScope.Target.Invoke(director, null)).InnerException!;
        Assert.IsType<NullReferenceException>(native);
        try
        {
            harmony.Patch(BlueprintEvaluationScope.Target,
                prefix: new HarmonyMethod(typeof(BlueprintEvaluationScope), nameof(BlueprintEvaluationScope.Prefix)),
                transpiler: new HarmonyMethod(typeof(BlueprintEvaluationScope), nameof(BlueprintEvaluationScope.Transpiler)),
                finalizer: new HarmonyMethod(typeof(BlueprintEvaluationScope), nameof(BlueprintEvaluationScope.Finalizer)));
            BlueprintToggleEvaluation.BeginValidatedUpdate();
            Assert.True(BlueprintToggleEvaluation.IsValidatedUpdate);
            Exception patched = Assert.Throws<TargetInvocationException>(() => BlueprintEvaluationScope.Target.Invoke(director, null)).InnerException!;
            Assert.Equal(native.GetType(), patched.GetType());
            Assert.False(BlueprintToggleEvaluation.IsValidatedUpdate);
        }
        finally
        {
            BlueprintToggleEvaluation.EndValidatedUpdate();
            harmony.UnpatchAll(owner);
        }
    }
}
