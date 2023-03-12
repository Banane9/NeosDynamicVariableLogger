using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using BaseX;
using CloudX.Shared;
using CodeX;
using FrooxEngine;
using FrooxEngine.LogiX;
using FrooxEngine.LogiX.Data;
using FrooxEngine.LogiX.Input;
using FrooxEngine.LogiX.ProgramFlow;
using FrooxEngine.LogiX.Quantity;
using FrooxEngine.UIX;
using HarmonyLib;
using NeosModLoader;
using NeosModLoader.Utility;

namespace DynamicVariableLogger
{
    public class DynamicVariableLogger : NeosMod
    {
        public static ModConfiguration Config;

        [AutoRegisterConfigKey]
        private static readonly ModConfigurationKey<bool> EnableDynamicVariableLogger = new ModConfigurationKey<bool>("EnableDynamicVariableLogger", "Enable dynamic variable (space) change logging.", () => true);

        private static readonly Type[] NeosPrimitiveAndEnumTypes;

        public override string Author => "Banane9";
        public override string Link => "https://github.com/Banane9/NeosDynamicVariableLogger";
        public override string Name => "DynamicVariableLogger";
        public override string Version => "1.0.0";

        static DynamicVariableLogger()
        {
            var traverse = Traverse.Create(typeof(GenericTypes));

            var neosPrimitiveTypes = traverse.Field<Type[]>("neosPrimitives").Value
                .Where(type => type.Name != "String")
                .AddItem(typeof(Rect))
                .AddItem(typeof(dummy))
                .AddItem(typeof(object))
                .ToArray();

            var neosEnumTypes = AccessTools.GetTypesFromAssembly(typeof(EnumInput<>).Assembly)
                .Concat(AccessTools.GetTypesFromAssembly(typeof(float4).Assembly))
                .Concat(AccessTools.GetTypesFromAssembly(typeof(SessionAccessLevel).Assembly))
                .Where(type => type.IsEnum)
                .Where(PublicTypesFilter)
                .Where(NoGenericTypesFilter)
                .ToArray();

            NeosPrimitiveAndEnumTypes = neosPrimitiveTypes.Concat(neosEnumTypes).ToArray();
        }

        public static IEnumerable<MethodBase> GenerateGenericMethodTargets(IEnumerable<Type> genericTypes, string methodName, params Type[] baseTypes)
        {
            return GenerateGenericMethodTargets(genericTypes, methodName, (IEnumerable<Type>)baseTypes);
        }

        public static IEnumerable<MethodBase> GenerateGenericMethodTargets(IEnumerable<Type> genericTypes, string methodName, IEnumerable<Type> baseTypes)
        {
            return GenerateMethodTargets(methodName,
                genericTypes.SelectMany(type => baseTypes.Select(baseType => baseType.MakeGenericType(type))));
        }

        public static IEnumerable<MethodBase> GenerateMethodTargets(string methodName, params Type[] baseTypes)
        {
            return GenerateMethodTargets(methodName, (IEnumerable<Type>)baseTypes);
        }

        public static IEnumerable<MethodBase> GenerateMethodTargets(string methodName, IEnumerable<Type> baseTypes)
        {
            return baseTypes.Select(type => type.GetMethod(methodName, AccessTools.allDeclared)).Where(m => m != null);
        }

        public override void OnEngineInit()
        {
            var harmony = new Harmony($"{Author}.{Name}");
            Config = GetConfiguration();
            Config.Save(true);

            if (Config.GetValue(EnableDynamicVariableLogger))
                harmony.PatchAll();
        }

        private static bool GenericTypesFilter(Type type)
        {
            return (!type.IsNested && type.IsGenericType)
                   || (type.IsNested && (type.IsGenericType || GenericTypesFilter(type.DeclaringType)));
        }

        private static bool NoGenericTypesFilter(Type type)
        {
            return !GenericTypesFilter(type);
        }

        private static bool PublicTypesFilter(Type type)
        {
            return (!type.IsNested && type.IsPublic)
                   || (type.IsNested && type.IsNestedPublic && PublicTypesFilter(type.DeclaringType));
        }

        [HarmonyPatch(typeof(DynamicVariableSpace))]
        private static class DynamicVariableSpacePatches
        {
            [HarmonyPrefix]
            [HarmonyPatch(nameof(DynamicVariableSpace.UpdateName))]
            private static void UpdateNamePrefix(DynamicVariableSpace __instance)
            {
                var newName = DynamicVariableHelper.ProcessName(__instance.SpaceName.Value);

                if (newName == __instance._lastName && __instance._lastNameSet)
                    return;

                Msg($"Changed name of DynamicVariableSpace [{__instance.ReferenceID}] from [{__instance._lastName}] to [{__instance.SpaceName.Value}].");
            }
        }

        [HarmonyPatch]
        private static class ValueManagerSetValuePatch
        {
            private static readonly GenericMethodInvoker setValueMethodInvoker = new(typeof(ValueManagerSetValuePatch).GetMethod("SetValue", AccessTools.all));

            private static bool Prefix(DynamicVariableSpace.ValueManager __instance, object value)
            {
                try
                {
                    setValueMethodInvoker.Invoke(__instance.GetType().GenericTypeArguments[0], __instance, value);
                }
                catch (Exception e)
                {
                    Msg(e);
                }
                return false;
            }

            private static string prettyPrint<T>(T value)
            {
                if (value == null)
                    return "null";

                return value is IWorldElement element ? $"{element.Name} ({element.ReferenceID})" : value.ToString();
            }

            private static void SetValue<T>(DynamicVariableSpace.ValueManager<T> manager, T value)
            {
                var nonNull = manager.lastValue ?? value;

                if (nonNull != null && !nonNull.Equals(value))
                    Msg($"Changed value of [{manager.Space.SpaceName.Value}/{manager.Name}<{typeof(T).Name}> ({manager.Space.ReferenceID})] from [{prettyPrint(manager.lastValue)}] to [{prettyPrint(value)}].");

                manager.lastValue = value;

                foreach (var variable in manager.values)
                    if (!variable.IsRemoved)
                        variable.DynamicValue = value;
            }

            private static IEnumerable<MethodBase> TargetMethods()
            {
                return GenerateGenericMethodTargets(
                    NeosPrimitiveAndEnumTypes,
                    nameof(DynamicVariableSpace.ValueManager<object>.SetValue),
                    typeof(DynamicVariableSpace.ValueManager<>));
            }
        }
    }
}