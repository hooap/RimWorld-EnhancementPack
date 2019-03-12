﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Reflection;
using System.Reflection.Emit;
using Verse;
using Verse.AI;
using Harmony;
using UnityEngine;
using TD.Utilities;

namespace TD_Enhancement_Pack
{
	[StaticConstructorOnStartup]
	static class ColorVariation
	{
		static ColorVariation()
		{
			HarmonyInstance harmony = HarmonyInstance.Create("Uuugggg.rimworld.TD_Enhancement_Pack.main");

			//patch Toils_Recipe.FinishRecipeAndStartStoringProduct
			MethodInfo MakeRecipeProductsInfo = AccessTools.Method(typeof(GenRecipe), nameof(GenRecipe.MakeRecipeProducts));

			harmony.PatchGeneratedMethod(typeof(Toils_Recipe),
				delegate (MethodInfo method)
				{
					DynamicMethod dm = DynamicTools.CreateDynamicMethod(method, "-unused");

					return (Harmony.ILCopying.MethodBodyReader.GetInstructions(dm.GetILGenerator(), method).
						Any(ilcode => ilcode.operand == MakeRecipeProductsInfo));
				},
				transpiler: new HarmonyMethod(typeof(ColorVariation), nameof(Toils_Recipe_Transpiler)));

			//patch GenRecipe.MakeRecipeProducts
			MethodInfo GetDrawColorInfo = AccessTools.Property(typeof(Thing), nameof(Thing.DrawColor)).GetGetMethod();

			harmony.PatchGeneratedMethod(typeof(GenRecipe),
				delegate (MethodInfo method)
				{
					DynamicMethod dm = DynamicTools.CreateDynamicMethod(method, "-unused");

					return (Harmony.ILCopying.MethodBodyReader.GetInstructions(dm.GetILGenerator(), method).
						Any(ilcode => ilcode.operand == GetDrawColorInfo));
				},
				transpiler: new HarmonyMethod(typeof(ColorVariation), nameof(GenRecipe_Transpiler)));
		}

		//[HarmonyPatch(typeof(Toils_Recipe), nameof(Toils_Recipe.FinishRecipeAndStartStoringProduct))]
		//actually patching compiler generated method for 'initAction =' delegate
		public static IEnumerable<CodeInstruction> Toils_Recipe_Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			MethodInfo MakeRecipeProductsInfo = AccessTools.Method(typeof(GenRecipe), nameof(GenRecipe.MakeRecipeProducts));

			foreach (CodeInstruction i in instructions)
			{
				yield return i;

				if (i.opcode == OpCodes.Call && i.operand == MakeRecipeProductsInfo)
				{
					yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ColorVariation), nameof(Variate)));
				}
			}
		}

		public static IEnumerable<Thing> Variate(IEnumerable<Thing> things)
		{
			foreach (Thing t in things)
			{
				VaryColor(t);
				yield return t;
			}
		}

		public static void VaryColor(Thing t)
		{
			if (!Settings.Get().colorGenerator && !Settings.Get().colorVariation) return;

			if (t is ThingWithComps thing)
			{
				if (thing.GetComp<CompColorable>() is CompColorable comp)// && thing.DrawColor != Color.white)// && comp.Active)
				{
					Log.Message($"{thing} color was {thing.DrawColor}/{comp.Active}:{comp.Color}");
					Color color = thing.DrawColor;

					//Use Color generator.
					//This is sorta redundant since the recipe just overwrote what might've been a color generated color, 
					//but only cloth allows it, but since it's overwritten it doesn't matter that cloth allows it. Aeh.
					if (Settings.Get().colorGenerator)
						if (!thing.def.MadeFromStuff && thing.def.colorGenerator != null)
						{
							if (Rand.Value < Settings.Get().colorGenChance) //.2 = 20% chance to colorgenerate instead of stuff color
								color = thing.def.colorGenerator.NewRandomizedColor();
						}

					if (Settings.Get().colorVariation)
					{
						//Deviate a little.
						Color.RGBToHSV(color, out float h, out float s, out float v);
						//hsv makes colored things look more varied, and whiter things less varied.
						color = Color.HSVToRGB(
						Mathf.Clamp01(h + (Rand.Value - 0.5f) / 10),
						Mathf.Clamp01(s + (Rand.Value - 0.5f) / 10),
						Mathf.Clamp01(v + (Rand.Value - 0.5f) / 10));
					}

					if (color != thing.DrawColor)
						comp.Color = color;
					Log.Message($"{thing} color now {thing.DrawColor}/{comp.Active}:{comp.Color}");
				}
			}
		}

		//public static IEnumerable<Thing> MakeRecipeProducts(RecipeDef recipeDef, Pawn worker, List<Thing> ingredients, Thing dominantIngredient, IBillGiver billGiver)
		//actually patching compiler generated method due to yield return business
		public static IEnumerable<CodeInstruction> GenRecipe_Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			MethodInfo GetDrawColorInfo = AccessTools.Property(typeof(Thing), nameof(Thing.DrawColor)).GetGetMethod();

			foreach (CodeInstruction i in instructions)
			{
				if (i.opcode == OpCodes.Callvirt && i.operand == GetDrawColorInfo)
				{
					yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ColorVariation), nameof(StuffColor)));
				}
				else
					yield return i;

			}
		}

		//public virtual Color DrawColor
		public static Color StuffColor(Thing stuff)
		{
			if (!Settings.Get().colorFixStuffColor) return stuff.DrawColor;

			return stuff.def.stuffProps?.color ?? stuff.DrawColor;
		}
	}

	[HarmonyPatch(typeof(Toils_Recipe), "CalculateDominantIngredient")]
	public static class Dominate
	{
		//private static Thing CalculateDominantIngredient(Job job, List<Thing> ingredients)
		public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			return Harmony.Transpilers.MethodReplacer(instructions,
				AccessTools.Method(typeof(GenCollection), "RandomElementByWeight").MakeGenericMethod(typeof(Thing)),
				AccessTools.Method(typeof(Dominate), nameof(StuffOnly)));
		}

		//public static T RandomElementByWeight<T>(this IEnumerable<T> source, Func<T, float> weightSelector)
		public static Thing StuffOnly(this IEnumerable<Thing> source, Func<Thing, float> weightSelector)
		{
			if (!Settings.Get().colorFixDominant) return source.RandomElementByWeight(weightSelector);

			List<Thing> stuffThings = source.Where(t => t.def.IsStuff).ToList();
			if (stuffThings.NullOrEmpty())
				return source.RandomElementByWeight(weightSelector);
			return stuffThings.RandomElementByWeight(weightSelector);
			//return source.MaxBy(weightSelector);	//only the one ever
		}
	}

	public static class ReapplyAll
	{
		public static void ReDo(IEnumerable<Thing> things)
		{
			//Re-do recipe color making
			foreach(Thing thing in things.Where(t => t.TryGetComp<CompColorable>() != null && (!t.def.costList.NullOrEmpty() || t.def.costStuffCount > 0)))
			{
				Log.Message($"REDOING {thing}");
				if (thing.def.MadeFromStuff)
					thing.SetColor(thing.Stuff.stuffProps.color);
				else
					thing.SetColor(thing.def.costList?.Where(c => c.thingDef.IsStuff).RandomElementByWeightWithFallback(c => c.count)?.thingDef.stuffProps.color ?? thing.DrawColor);
				ColorVariation.VaryColor(thing);
			}
			//Variation
		}
		public static void Go()
		{
			Log.Message("GOING");
			foreach(Map map in Find.Maps)
			{
				foreach(Pawn pawn in map.mapPawns.FreeColonists)
				{
					Log.Message($"MR {pawn}");
					ReDo(pawn.inventory.GetDirectlyHeldThings());
					ReDo(pawn.apparel.WornApparel.Cast<Thing>());
				}

				ReDo(map.listerThings.AllThings);
			}
		}
	}
}
