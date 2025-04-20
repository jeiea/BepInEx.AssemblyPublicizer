using AsmResolver;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures.Types;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables.Rows;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BepInEx.AssemblyPublicizer;

public static class AssemblyPublicizer
{
    public static void Publicize(string assemblyPath, string outputPath, AssemblyPublicizerOptions? options = null)
    {
        var assembly = FatalAsmResolver.FromFile(assemblyPath);
        var module = assembly.ManifestModule ?? throw new NullReferenceException();
        module.MetadataResolver = new DefaultMetadataResolver(NoopAssemblyResolver.Instance);

        Publicize(assembly, options);
        module.FatalWrite(outputPath);
    }

    public static AssemblyDefinition Publicize(AssemblyDefinition assembly, AssemblyPublicizerOptions? options = null)
    {
        options ??= new AssemblyPublicizerOptions();

        var module = assembly.ManifestModule!;

        var attribute = options.IncludeOriginalAttributesAttribute ? new OriginalAttributesAttribute(module) : null;

        foreach (var typeDefinition in module.GetAllTypes())
        {
            if (attribute != null && typeDefinition == attribute.Type)
                continue;

            Publicize(typeDefinition, attribute, options);
        }

        return assembly;
    }

    private static void Publicize(TypeDefinition typeDefinition, OriginalAttributesAttribute? attribute, AssemblyPublicizerOptions options)
    {
        if (options.Strip && !typeDefinition.IsEnum && !typeDefinition.IsInterface)
        {
            foreach (var methodDefinition in typeDefinition.Methods)
            {
                if (!methodDefinition.HasMethodBody)
                    continue;

                var newBody = methodDefinition.CilMethodBody = new CilMethodBody(methodDefinition);
                if (methodDefinition.Signature?.ReturnType is TypeSignature returnType)
                {
                    foreach (var instruction in GetStub(returnType))
                    {
                        newBody.Instructions.Add(instruction);
                    }
                }
                else
                {
                    newBody.Instructions.Add(CilOpCodes.Ldnull);
                    newBody.Instructions.Add(CilOpCodes.Throw);
                }
                methodDefinition.NoInlining = true;
            }
        }

        if (!options.PublicizeCompilerGenerated && typeDefinition.IsCompilerGenerated())
            return;

        if (options.HasTarget(PublicizeTarget.Types) && (!typeDefinition.IsNested && !typeDefinition.IsPublic || typeDefinition.IsNested && !typeDefinition.IsNestedPublic))
        {
            if (attribute != null)
                typeDefinition.CustomAttributes.Add(attribute.ToCustomAttribute(typeDefinition.Attributes & TypeAttributes.VisibilityMask));

            typeDefinition.Attributes &= ~TypeAttributes.VisibilityMask;
            typeDefinition.Attributes |= typeDefinition.IsNested ? TypeAttributes.NestedPublic : TypeAttributes.Public;
        }

        if (options.HasTarget(PublicizeTarget.Methods))
        {
            foreach (var methodDefinition in typeDefinition.Methods)
            {
                Publicize(methodDefinition, attribute, options);
            }

            // Special case for accessors generated from auto properties, publicize them regardless of PublicizeCompilerGenerated
            if (!options.PublicizeCompilerGenerated)
            {
                foreach (var propertyDefinition in typeDefinition.Properties)
                {
                    if (propertyDefinition.IsCompilerGenerated()) continue;

                    if (propertyDefinition.GetMethod is { } getMethod) Publicize(getMethod, attribute, options, true);
                    if (propertyDefinition.SetMethod is { } setMethod) Publicize(setMethod, attribute, options, true);
                }
            }
        }

        if (options.HasTarget(PublicizeTarget.Fields))
        {
            var eventNames = new HashSet<Utf8String?>(typeDefinition.Events.Select(e => e.Name));
            foreach (var fieldDefinition in typeDefinition.Fields)
            {
                if (fieldDefinition.IsPrivateScope)
                    continue;

                if (!fieldDefinition.IsPublic)
                {
                    // Skip event backing fields
                    if (eventNames.Contains(fieldDefinition.Name))
                        continue;

                    if (!options.PublicizeCompilerGenerated && fieldDefinition.IsCompilerGenerated())
                        continue;

                    if (attribute != null)
                        fieldDefinition.CustomAttributes.Add(attribute.ToCustomAttribute(fieldDefinition.Attributes & FieldAttributes.FieldAccessMask));

                    fieldDefinition.Attributes &= ~FieldAttributes.FieldAccessMask;
                    fieldDefinition.Attributes |= FieldAttributes.Public;
                }
            }
        }
    }

    private static IEnumerable<CilOpCode> GetStub(TypeSignature returnType)
    {
        switch (returnType.ElementType)
        {
            case ElementType.Boolean:
            case ElementType.Char:
            case ElementType.I1:
            case ElementType.U1:
            case ElementType.I2:
            case ElementType.U2:
            case ElementType.I4:
            case ElementType.U4:
                yield return CilOpCodes.Ldc_I4_0;
                yield return CilOpCodes.Ret;
                yield break;

            case ElementType.I8:
            case ElementType.U8:
                yield return CilOpCodes.Ldc_I4_0;
                yield return CilOpCodes.Conv_I8;
                yield return CilOpCodes.Ret;
                yield break;

            case ElementType.R4:
                yield return CilOpCodes.Ldc_I4_0;
                yield return CilOpCodes.Conv_R4;
                yield return CilOpCodes.Ret;
                yield break;
            case ElementType.R8:
                yield return CilOpCodes.Ldc_I4_0;
                yield return CilOpCodes.Conv_R8;
                yield return CilOpCodes.Ret;
                yield break;

            case ElementType.Void:
                yield return CilOpCodes.Ret;
                yield break;

            case ElementType.String:
            default:
                yield return CilOpCodes.Ldnull;
                yield return CilOpCodes.Ret;
                yield break;
        }
    }

    private static void Publicize(MethodDefinition methodDefinition, OriginalAttributesAttribute? attribute, AssemblyPublicizerOptions options, bool ignoreCompilerGeneratedCheck = false)
    {
        if (methodDefinition.IsCompilerControlled)
            return;

        // Ignore explicit interface implementations because you can't call them directly anyway and it confuses IDEs
        if (methodDefinition is { IsVirtual: true, IsFinal: true, DeclaringType: not null })
        {
            foreach (var implementation in methodDefinition.DeclaringType.MethodImplementations)
            {
                if (implementation.Body == methodDefinition)
                {
                    return;
                }
            }
        }

        if (!methodDefinition.IsPublic)
        {
            if (!ignoreCompilerGeneratedCheck && !options.PublicizeCompilerGenerated && methodDefinition.IsCompilerGenerated())
                return;

            if (attribute != null)
                methodDefinition.CustomAttributes.Add(attribute.ToCustomAttribute(methodDefinition.Attributes & MethodAttributes.MemberAccessMask));

            methodDefinition.Attributes &= ~MethodAttributes.MemberAccessMask;
            methodDefinition.Attributes |= MethodAttributes.Public;
        }
    }
}
