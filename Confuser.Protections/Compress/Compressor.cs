﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Confuser.Core;
using Confuser.Protections.Compress;
using dnlib.DotNet;
using Confuser.Core.Services;
using Confuser.Core.Helpers;
using dnlib.DotNet.Emit;
using System.Diagnostics;
using System.IO;
using dnlib.DotNet.Writer;
using dnlib.DotNet.MD;
using System.Security.Cryptography;

namespace Confuser.Protections
{
    class Compressor : Packer
    {
        public const string _Id = "compressor";
        public const string _FullId = "Ki.Compressor";
        public const string _ServiceId = "Ki.Compressor";

        protected override void Initialize(ConfuserContext context)
        {
            //context.Registry.RegisterService(_ServiceId, typeof(IControlFlowService), this);
        }

        protected override void PopulatePipeline(ProtectionPipeline pipeline)
        {
            pipeline.InsertPreStage(PipelineStage.WriteModule, new ExtractPhase(this));
        }

        public static readonly object ContextKey = new object();

        protected override void Pack(ConfuserContext context, ProtectionParameters parameters)
        {
            var ctx = context.Annotations.Get<CompressorContext>(context, ContextKey);
            if (ctx == null)
            {
                context.Logger.Error("No main module specified!");
                throw new ConfuserException(null);
            }

            var originModule = context.Modules[ctx.ModuleIndex];
            var stubModule = new ModuleDefUser(ctx.ModuleName, originModule.Mvid, originModule.CorLibTypes.AssemblyRef);
            ctx.Assembly.Modules.Insert(0, stubModule);
            stubModule.Characteristics = originModule.Characteristics;
            stubModule.Cor20HeaderFlags = originModule.Cor20HeaderFlags;
            stubModule.Cor20HeaderRuntimeVersion = originModule.Cor20HeaderRuntimeVersion;
            stubModule.DllCharacteristics = originModule.DllCharacteristics;
            stubModule.EncBaseId = originModule.EncBaseId;
            stubModule.EncId = originModule.EncId;
            stubModule.Generation = originModule.Generation;
            stubModule.Kind = ctx.Kind;
            stubModule.Machine = originModule.Machine;
            stubModule.RuntimeVersion = originModule.RuntimeVersion;
            stubModule.TablesHeaderVersion = originModule.TablesHeaderVersion;
            stubModule.Win32Resources = originModule.Win32Resources;

            InjectStub(context, ctx, parameters, stubModule);

            var snKey = context.Annotations.Get<StrongNameKey>(originModule, Marker.SNKey);
            using (MemoryStream ms = new MemoryStream())
            {
                stubModule.Write(ms, new ModuleWriterOptions(stubModule, new KeyInjector(ctx))
                {
                    StrongNameKey = snKey
                });
                base.ProtectStub(context, context.OutputPaths[ctx.ModuleIndex], ms.ToArray(), snKey, new StubProtection(ctx));
            }
        }

        void PackModules(ConfuserContext context, CompressorContext compCtx, ModuleDef stubModule, ICompressionService comp, RandomGenerator random)
        {
            int maxLen = 0;
            var modules = new Dictionary<string, byte[]>();
            for (int i = 0; i < context.OutputModules.Count; i++)
            {
                if (i == compCtx.ModuleIndex)
                    continue;

                string fullName = context.Modules[i].Assembly.FullName;
                modules.Add(fullName, context.OutputModules[i]);

                int strLen = Encoding.UTF8.GetByteCount(fullName);
                if (strLen > maxLen)
                    maxLen = strLen;
            }

            byte[] key = random.NextBytes(4 + maxLen);
            key[0] = (byte)(compCtx.EntryPointToken >> 0);
            key[1] = (byte)(compCtx.EntryPointToken >> 8);
            key[2] = (byte)(compCtx.EntryPointToken >> 16);
            key[3] = (byte)(compCtx.EntryPointToken >> 24);
            for (int i = 4; i < key.Length; i++)   // no zero bytes
                key[i] |= 1;
            compCtx.KeySig = key;

            foreach (var entry in modules)
            {
                byte[] name = Encoding.UTF8.GetBytes(entry.Key);
                for (int i = 0; i < name.Length; i++)
                    name[i] *= key[i + 4];

                uint state = 0x6fff61;
                foreach (var chr in name)
                    state = state * 0x5e3f1f + chr;
                var encrypted = compCtx.Encrypt(comp, entry.Value, state);

                var resource = new EmbeddedResource(Convert.ToBase64String(name), encrypted, ManifestResourceAttributes.Private);
                stubModule.Resources.Add(resource);
            }
        }

        void InjectData(ModuleDef stubModule, MethodDef method, byte[] data)
        {
            var dataType = new TypeDefUser("", "DataType", stubModule.CorLibTypes.GetTypeRef("System", "ValueType"));
            dataType.Layout = TypeAttributes.ExplicitLayout;
            dataType.Visibility = TypeAttributes.NestedPrivate;
            dataType.IsSealed = true;
            dataType.ClassLayout = new ClassLayoutUser(1, (uint)data.Length);
            stubModule.GlobalType.NestedTypes.Add(dataType);

            var dataField = new FieldDefUser("DataField", new FieldSig(dataType.ToTypeSig()))
            {
                IsStatic = true,
                HasFieldRVA = true,
                InitialValue = data,
                Access = FieldAttributes.CompilerControlled
            };
            stubModule.GlobalType.Fields.Add(dataField);

            MutationHelper.ReplacePlaceholder(method, arg =>
            {
                List<Instruction> repl = new List<Instruction>();
                repl.AddRange(arg);
                repl.Add(Instruction.Create(OpCodes.Dup));
                repl.Add(Instruction.Create(OpCodes.Ldtoken, dataField));
                repl.Add(Instruction.Create(OpCodes.Call, stubModule.Import(
                    typeof(System.Runtime.CompilerServices.RuntimeHelpers).GetMethod("InitializeArray"))));
                return repl.ToArray();
            });
        }

        void InjectStub(ConfuserContext context, CompressorContext compCtx, ProtectionParameters parameters, ModuleDef stubModule)
        {
            var rt = context.Registry.GetService<IRuntimeService>();
            var random = context.Registry.GetService<IRandomService>().GetRandomGenerator(Id);
            var comp = context.Registry.GetService<ICompressionService>();
            var defs = InjectHelper.Inject(rt.GetRuntimeType("Confuser.Runtime.Compressor"), stubModule.GlobalType, stubModule);

            switch (parameters.GetParameter<Mode>(context, context.CurrentModule, "key", Mode.Normal))
            {
                case Mode.Normal:
                    compCtx.Deriver = new NormalDeriver();
                    break;
                case Mode.Dynamic:
                    compCtx.Deriver = new DynamicDeriver();
                    break;
                default:
                    throw new UnreachableException();
            }
            compCtx.Deriver.Init(context, random);

            // Main
            var entryPoint = defs.OfType<MethodDef>().Single(method => method.Name == "Main");
            stubModule.EntryPoint = entryPoint;

            var seed = random.NextUInt32();
            compCtx.OriginModule = context.OutputModules[compCtx.ModuleIndex];
            var encryptedModule = compCtx.Encrypt(comp, compCtx.OriginModule, seed);
            compCtx.EncryptedModule = encryptedModule;

            MutationHelper.InjectKeys(entryPoint,
                new int[] { 0, 1 },
                new int[] { encryptedModule.Length >> 2, (int)seed });
            InjectData(stubModule, entryPoint, encryptedModule);

            // Decrypt
            var decrypter = defs.OfType<MethodDef>().Single(method => method.Name == "Decrypt");
            decrypter.Body.SimplifyMacros(decrypter.Parameters);
            var instrs = decrypter.Body.Instructions.ToList();
            for (int i = 0; i < instrs.Count; i++)
            {
                var instr = instrs[i];
                if (instr.OpCode == OpCodes.Call)
                {
                    IMethod method = (IMethod)instr.Operand;
                    if (method.DeclaringType.Name == "Mutation" &&
                       method.Name == "Crypt")
                    {
                        Instruction ldDst = instrs[i - 2];
                        Instruction ldSrc = instrs[i - 1];
                        Debug.Assert(ldDst.OpCode == OpCodes.Ldloc && ldSrc.OpCode == OpCodes.Ldloc);
                        instrs.RemoveAt(i);
                        instrs.RemoveAt(i - 1);
                        instrs.RemoveAt(i - 2);
                        instrs.InsertRange(i - 2, compCtx.Deriver.EmitDerivation(decrypter, context, (Local)ldDst.Operand, (Local)ldSrc.Operand));
                    }
                    else if (method.DeclaringType.Name == "Lzma" &&
                       method.Name == "Decompress")
                    {
                        var decomp = comp.GetRuntimeDecompressor(stubModule, member => { });
                        instr.Operand = decomp;
                    }
                }
            }
            decrypter.Body.Instructions.Clear();
            foreach (var instr in instrs)
                decrypter.Body.Instructions.Add(instr);

            // Pack modules
            PackModules(context, compCtx, stubModule, comp, random);
        }

        class KeyInjector : IModuleWriterListener
        {
            CompressorContext ctx;
            public KeyInjector(CompressorContext ctx)
            {
                this.ctx = ctx;
            }

            public void OnWriterEvent(ModuleWriterBase writer, ModuleWriterEvent evt)
            {
                if (evt == ModuleWriterEvent.MDBeginCreateTables)
                {
                    // Add key signature
                    uint sigBlob = writer.MetaData.BlobHeap.Add(ctx.KeySig);
                    uint sigRid = writer.MetaData.TablesHeap.StandAloneSigTable.Add(new RawStandAloneSigRow(sigBlob));
                    Debug.Assert(sigRid == 1);
                    uint sigToken = 0x11000000 | sigRid;
                    ctx.KeyToken = sigToken;
                    MutationHelper.InjectKey(writer.Module.EntryPoint, 2, (int)sigToken);
                }
                else if (evt == ModuleWriterEvent.MDBeginAddResources)
                {
                    // Compute hash
                    var hash = SHA1Managed.Create().ComputeHash(ctx.OriginModule);
                    var hashBlob = writer.MetaData.BlobHeap.Add(hash);

                    var fileTbl = writer.MetaData.TablesHeap.FileTable;
                    var fileRid = fileTbl.Add(new RawFileRow(
                        (uint)dnlib.DotNet.FileAttributes.ContainsMetaData,
                        writer.MetaData.StringsHeap.Add("koi"),
                        hashBlob));
                    var impl = CodedToken.Implementation.Encode(new MDToken(Table.File, fileRid));

                    // Add resources
                    var resTbl = writer.MetaData.TablesHeap.ManifestResourceTable;
                    foreach (var resource in ctx.ManifestResources)
                        resTbl.Add(new RawManifestResourceRow(resource.Item1, resource.Item2, writer.MetaData.StringsHeap.Add(resource.Item3), impl));
                }
            }
        }

        public override string Name
        {
            get { return "Compressing Packer"; }
        }

        public override string Description
        {
            get { return "This packer reduces the size of output."; }
        }

        public override string Id
        {
            get { return _Id; }
        }

        public override string FullId
        {
            get { return _FullId; }
        }
    }
}