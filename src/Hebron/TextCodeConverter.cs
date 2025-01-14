﻿using ClangSharp;
using ClangSharp.Interop;

namespace Hebron
{
	public static class TextCodeConverter
	{
		public static string Convert(string inputPath, string[] defines)
		{
			var translationUnit = Utility.Compile(inputPath, defines);

			var writer = new IndentedStringWriter();
			foreach (var cursor in translationUnit.EnumerateCursors())
			{
				DumpCursor(writer, cursor);
			}

			return writer.Result;
		}

		private static void DumpCursor(IndentedStringWriter writer, Cursor cursor)
		{
			var line = string.Format("// {0}- {1} - {2}", cursor.CursorKindSpelling,
				cursor.Spelling,
				clang.getTypeSpelling(clang.getCursorType(cursor.Handle)));

			var addition = string.Empty;

			switch (cursor.CursorKind)
			{
				case CXCursorKind.CXCursor_UnaryExpr:
				case CXCursorKind.CXCursor_UnaryOperator:
					{
						var opCode = clangsharp.Cursor_getUnaryOpcode(cursor.Handle);
						addition = string.Format("Unary Operator: {0} ({1})",
							opCode, clangsharp.Cursor_getUnaryOpcodeSpelling(opCode));
					}
					break;
				case CXCursorKind.CXCursor_BinaryOperator:
					{
						var opCode = clangsharp.Cursor_getBinaryOpcode(cursor.Handle);
						addition = string.Format("Binary Operator: {0} ({1})",
							opCode, clangsharp.Cursor_getBinaryOpcodeSpelling(opCode));
					}
					break;
				case CXCursorKind.CXCursor_IntegerLiteral:
				case CXCursorKind.CXCursor_FloatingLiteral:
				case CXCursorKind.CXCursor_CharacterLiteral:
				case CXCursorKind.CXCursor_StringLiteral:
				case CXCursorKind.CXCursor_CXXBoolLiteralExpr:
					addition = string.Format("Literal: {0}", cursor.Handle.GetLiteralString());
					break;
			}

			if (!string.IsNullOrEmpty(addition))
			{
				line += " [" + addition + "]";
			}

			writer.IndentedWriteLine(line);

			writer.IndentLevel++;
			foreach(var child in cursor.CursorChildren)
			{
				DumpCursor(writer, child);
			}
			writer.IndentLevel--;
		}
	}
}
