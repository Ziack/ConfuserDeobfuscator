﻿/*
    Copyright (C) 2011-2013 de4dot@gmail.com

    This file is part of de4dot.

    de4dot is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    de4dot is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with de4dot.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.IO;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using de4dot.blocks;

namespace de4dot.code.deobfuscators.Agile_NET.vm {
	class CsvmToCilMethodConverter {
		IDeobfuscatorContext deobfuscatorContext;
		ModuleDefMD module;
		VmOpCodeHandlerDetector opCodeDetector;
		CilOperandInstructionRestorer operandRestorer = new CilOperandInstructionRestorer();

		public CsvmToCilMethodConverter(IDeobfuscatorContext deobfuscatorContext, ModuleDefMD module, VmOpCodeHandlerDetector opCodeDetector) {
			this.deobfuscatorContext = deobfuscatorContext;
			this.module = module;
			this.opCodeDetector = opCodeDetector;
		}

		public void convert(MethodDef cilMethod, CsvmMethodData csvmMethod) {
			var newInstructions = readInstructions(cilMethod, csvmMethod);
			var newLocals = readLocals(cilMethod, csvmMethod);
			var newExceptions = readExceptions(cilMethod, csvmMethod, newInstructions);

			fixInstructionOperands(newInstructions);
			fixLocals(newInstructions, cilMethod.Body.Variables);
			fixArgs(newInstructions, cilMethod);

			DotNetUtils.restoreBody(cilMethod, newInstructions, newExceptions);

			if (!operandRestorer.restore(cilMethod))
				Logger.w("Failed to restore one or more instruction operands in CSVM method {0:X8}", cilMethod.MDToken.ToInt32());
			restoreConstrainedPrefix(cilMethod);
		}

		void fixLocals(IList<Instruction> instrs, IList<Local> locals) {
			foreach (var instr in instrs) {
				var op = instr.Operand as LocalOperand;
				if (op == null)
					continue;

				updateLocalInstruction(instr, locals[op.local], op.local);
			}
		}

		static void updateLocalInstruction(Instruction instr, Local local, int index) {
			object operand = null;
			OpCode opcode;

			switch (instr.OpCode.Code) {
			case Code.Ldloc_S:
			case Code.Ldloc:
				if (index == 0)
					opcode = OpCodes.Ldloc_0;
				else if (index == 1)
					opcode = OpCodes.Ldloc_1;
				else if (index == 2)
					opcode = OpCodes.Ldloc_2;
				else if (index == 3)
					opcode = OpCodes.Ldloc_3;
				else if (byte.MinValue <= index && index <= byte.MaxValue) {
					opcode = OpCodes.Ldloc_S;
					operand = local;
				}
				else {
					opcode = OpCodes.Ldloc;
					operand = local;
				}
				break;

			case Code.Stloc:
			case Code.Stloc_S:
				if (index == 0)
					opcode = OpCodes.Stloc_0;
				else if (index == 1)
					opcode = OpCodes.Stloc_1;
				else if (index == 2)
					opcode = OpCodes.Stloc_2;
				else if (index == 3)
					opcode = OpCodes.Stloc_3;
				else if (byte.MinValue <= index && index <= byte.MaxValue) {
					opcode = OpCodes.Stloc_S;
					operand = local;
				}
				else {
					opcode = OpCodes.Stloc;
					operand = local;
				}
				break;

			case Code.Ldloca:
			case Code.Ldloca_S:
				if (byte.MinValue <= index && index <= byte.MaxValue) {
					opcode = OpCodes.Ldloca_S;
					operand = local;
				}
				else {
					opcode = OpCodes.Ldloca;
					operand = local;
				}
				break;

			default:
				throw new ApplicationException("Invalid opcode");
			}

			instr.OpCode = opcode;
			instr.Operand = operand;
		}

		void fixArgs(IList<Instruction> instrs, MethodDef method) {
			foreach (var instr in instrs) {
				var op = instr.Operand as ArgOperand;
				if (op == null)
					continue;

				updateArgInstruction(instr, method.Parameters[op.arg], op.arg);
			}
		}

		static void updateArgInstruction(Instruction instr, Parameter arg, int index) {
			switch (instr.OpCode.Code) {
			case Code.Ldarg:
			case Code.Ldarg_S:
				if (index == 0) {
					instr.OpCode = OpCodes.Ldarg_0;
					instr.Operand = null;
				}
				else if (index == 1) {
					instr.OpCode = OpCodes.Ldarg_1;
					instr.Operand = null;
				}
				else if (index == 2) {
					instr.OpCode = OpCodes.Ldarg_2;
					instr.Operand = null;
				}
				else if (index == 3) {
					instr.OpCode = OpCodes.Ldarg_3;
					instr.Operand = null;
				}
				else if (byte.MinValue <= index && index <= byte.MaxValue) {
					instr.OpCode = OpCodes.Ldarg_S;
					instr.Operand = arg;
				}
				else {
					instr.OpCode = OpCodes.Ldarg;
					instr.Operand = arg;
				}
				break;

			case Code.Starg:
			case Code.Starg_S:
				if (byte.MinValue <= index && index <= byte.MaxValue) {
					instr.OpCode = OpCodes.Starg_S;
					instr.Operand = arg;
				}
				else {
					instr.OpCode = OpCodes.Starg;
					instr.Operand = arg;
				}
				break;

			case Code.Ldarga:
			case Code.Ldarga_S:
				if (byte.MinValue <= index && index <= byte.MaxValue) {
					instr.OpCode = OpCodes.Ldarga_S;
					instr.Operand = arg;
				}
				else {
					instr.OpCode = OpCodes.Ldarga;
					instr.Operand = arg;
				}
				break;

			default:
				throw new ApplicationException("Invalid opcode");
			}
		}

		List<Instruction> readInstructions(MethodDef cilMethod, CsvmMethodData csvmMethod) {
			var reader = new BinaryReader(new MemoryStream(csvmMethod.Instructions));
			var instrs = new List<Instruction>();
			uint offset = 0;
			while (reader.BaseStream.Position < reader.BaseStream.Length) {
				int vmOpCode = reader.ReadUInt16();
				var instr = opCodeDetector.Handlers[vmOpCode].Read(reader);
				instr.Offset = offset;
				offset += (uint)getInstructionSize(instr);
				instrs.Add(instr);
			}
			return instrs;
		}

		static int getInstructionSize(Instruction instr) {
			var opcode = instr.OpCode;
			if (opcode == null)
				return 5;	// Load store/field
			var op = instr.Operand as SwitchTargetDisplOperand;
			if (op == null)
				return instr.GetSize();
			return instr.OpCode.Size + (op.targetDisplacements.Length + 1) * 4;
		}

		List<Local> readLocals(MethodDef cilMethod, CsvmMethodData csvmMethod) {
			var locals = new List<Local>();
			var reader = new BinaryReader(new MemoryStream(csvmMethod.Locals));

			if (csvmMethod.Locals.Length == 0)
				return locals;

			// v6.0.0.5 sometimes duplicates the last two locals so only check for a negative value.
			int numLocals = reader.ReadInt32();
			if (numLocals < 0)
				throw new ApplicationException("Invalid number of locals");

			for (int i = 0; i < numLocals; i++)
				locals.Add(new Local(readTypeRef(reader)));

			return locals;
		}

		TypeSig readTypeRef(BinaryReader reader) {
			var etype = (ElementType)reader.ReadInt32();
			switch (etype) {
			case ElementType.Void: return module.CorLibTypes.Void;
			case ElementType.Boolean: return module.CorLibTypes.Boolean;
			case ElementType.Char: return module.CorLibTypes.Char;
			case ElementType.I1: return module.CorLibTypes.SByte;
			case ElementType.U1: return module.CorLibTypes.Byte;
			case ElementType.I2: return module.CorLibTypes.Int16;
			case ElementType.U2: return module.CorLibTypes.UInt16;
			case ElementType.I4: return module.CorLibTypes.Int32;
			case ElementType.U4: return module.CorLibTypes.UInt32;
			case ElementType.I8: return module.CorLibTypes.Int64;
			case ElementType.U8: return module.CorLibTypes.UInt64;
			case ElementType.R4: return module.CorLibTypes.Single;
			case ElementType.R8: return module.CorLibTypes.Double;
			case ElementType.String: return module.CorLibTypes.String;
			case ElementType.TypedByRef: return module.CorLibTypes.TypedReference;
			case ElementType.I: return module.CorLibTypes.IntPtr;
			case ElementType.U: return module.CorLibTypes.UIntPtr;
			case ElementType.Object: return module.CorLibTypes.Object;

			case ElementType.ValueType:
			case ElementType.Var:
			case ElementType.MVar:
				return (module.ResolveToken(reader.ReadUInt32()) as ITypeDefOrRef).ToTypeSig();

			case ElementType.GenericInst:
				etype = (ElementType)reader.ReadInt32();
				if (etype == ElementType.ValueType)
					return (module.ResolveToken(reader.ReadUInt32()) as ITypeDefOrRef).ToTypeSig();
				// ElementType.Class
				return module.CorLibTypes.Object;

			case ElementType.Ptr:
			case ElementType.Class:
			case ElementType.Array:
			case ElementType.FnPtr:
			case ElementType.SZArray:
			case ElementType.ByRef:
			case ElementType.CModReqd:
			case ElementType.CModOpt:
			case ElementType.Internal:
			case ElementType.Sentinel:
			case ElementType.Pinned:
			default:
				return module.CorLibTypes.Object;
			}
		}

		List<ExceptionHandler> readExceptions(MethodDef cilMethod, CsvmMethodData csvmMethod, List<Instruction> cilInstructions) {
			var reader = new BinaryReader(new MemoryStream(csvmMethod.Exceptions));
			var ehs = new List<ExceptionHandler>();

			if (reader.BaseStream.Length == 0)
				return ehs;

			int numExceptions = reader.ReadInt32();
			if (numExceptions < 0)
				throw new ApplicationException("Invalid number of exception handlers");

			for (int i = 0; i < numExceptions; i++) {
				var eh = new ExceptionHandler((ExceptionHandlerType)reader.ReadInt32());
				eh.TryStart = getInstruction(cilInstructions, reader.ReadInt32());
				eh.TryEnd = getInstructionEnd(cilInstructions, reader.ReadInt32());
				eh.HandlerStart = getInstruction(cilInstructions, reader.ReadInt32());
				eh.HandlerEnd = getInstructionEnd(cilInstructions, reader.ReadInt32());
				if (eh.HandlerType == ExceptionHandlerType.Catch)
					eh.CatchType = module.ResolveToken(reader.ReadUInt32()) as ITypeDefOrRef;
				else if (eh.HandlerType == ExceptionHandlerType.Filter)
					eh.FilterStart = getInstruction(cilInstructions, reader.ReadInt32());

				ehs.Add(eh);
			}

			return ehs;
		}

		static Instruction getInstruction(IList<Instruction> instrs, int index) {
			return instrs[index];
		}

		static Instruction getInstructionEnd(IList<Instruction> instrs, int index) {
			index++;
			if (index == instrs.Count)
				return null;
			return instrs[index];
		}

		static Instruction getInstruction(IList<Instruction> instrs, Instruction source, int displ) {
			int sourceIndex = instrs.IndexOf(source);
			if (sourceIndex < 0)
				throw new ApplicationException("Could not find source instruction");
			return instrs[sourceIndex + displ];
		}

		void fixInstructionOperands(IList<Instruction> instrs) {
			foreach (var instr in instrs) {
				var op = instr.Operand as IVmOperand;
				if (op != null)
					instr.Operand = fixOperand(instrs, instr, op);
			}
		}

		object fixOperand(IList<Instruction> instrs, Instruction instr, IVmOperand vmOperand) {
			if (vmOperand is TokenOperand)
				return getMemberRef(((TokenOperand)vmOperand).token);

			if (vmOperand is TargetDisplOperand)
				return getInstruction(instrs, instr, ((TargetDisplOperand)vmOperand).displacement);

			if (vmOperand is SwitchTargetDisplOperand) {
				var targetDispls = ((SwitchTargetDisplOperand)vmOperand).targetDisplacements;
				Instruction[] targets = new Instruction[targetDispls.Length];
				for (int i = 0; i < targets.Length; i++)
					targets[i] = getInstruction(instrs, instr, targetDispls[i]);
				return targets;
			}

			if (vmOperand is ArgOperand || vmOperand is LocalOperand)
				return vmOperand;

			if (vmOperand is LoadFieldOperand)
				return fixLoadStoreFieldInstruction(instr, ((LoadFieldOperand)vmOperand).token, OpCodes.Ldsfld, OpCodes.Ldfld);

			if (vmOperand is LoadFieldAddressOperand)
				return fixLoadStoreFieldInstruction(instr, ((LoadFieldAddressOperand)vmOperand).token, OpCodes.Ldsflda, OpCodes.Ldflda);

			if (vmOperand is StoreFieldOperand)
				return fixLoadStoreFieldInstruction(instr, ((StoreFieldOperand)vmOperand).token, OpCodes.Stsfld, OpCodes.Stfld);

			throw new ApplicationException(string.Format("Unknown operand type: {0}", vmOperand.GetType()));
		}

		IField fixLoadStoreFieldInstruction(Instruction instr, int token, OpCode staticInstr, OpCode instanceInstr) {
			var fieldRef = module.ResolveToken(token) as IField;
			var field = deobfuscatorContext.resolveField(fieldRef);
			bool isStatic;
			if (field == null) {
				Logger.w("Could not resolve field {0:X8}. Assuming it's not static.", token);
				isStatic = false;
			}
			else
				isStatic = field.IsStatic;
			instr.OpCode = isStatic ? staticInstr : instanceInstr;
			return fieldRef;
		}

		ITokenOperand getMemberRef(int token) {
			var memberRef = module.ResolveToken(token) as ITokenOperand;
			if (memberRef == null)
				throw new ApplicationException(string.Format("Could not find member ref: {0:X8}", token));
			return memberRef;
		}

		static void restoreConstrainedPrefix(MethodDef method) {
			if (method == null || method.Body == null)
				return;

			var instrs = method.Body.Instructions;
			for (int i = 0; i < instrs.Count; i++) {
				var instr = instrs[i];
				if (instr.OpCode.Code != Code.Callvirt)
					continue;

				var calledMethod = instr.Operand as IMethod;
				if (calledMethod == null)
					continue;
				var sig = calledMethod.MethodSig;
				if (sig == null || !sig.HasThis)
					continue;
				var thisType = MethodStack.getLoadedType(method, instrs, i, sig.Params.Count) as ByRefSig;
				if (thisType == null)
					continue;
				if (hasPrefix(instrs, i, Code.Constrained))
					continue;
				instrs.Insert(i, OpCodes.Constrained.ToInstruction(thisType.Next.ToTypeDefOrRef()));
				i++;
			}
		}

		static bool hasPrefix(IList<Instruction> instrs, int index, Code prefix) {
			index--;
			for (; index >= 0; index--) {
				var instr = instrs[index];
				if (instr.OpCode.OpCodeType != OpCodeType.Prefix)
					break;
				if (instr.OpCode.Code == prefix)
					return true;
			}
			return false;
		}
	}
}
