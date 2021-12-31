﻿using ClangSharp;
using ClangSharp.Interop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Type = ClangSharp.Type;

namespace Hebron.Rust
{
	partial class RustCodeConverter
	{
		private const string NullPtr = "std::ptr::null_mut()";

		private class FieldInfo
		{
			public string Name;
			public Type Type;
		}

		private FunctionDecl _functionDecl;
		private List<FieldInfo> _currentStructInfo;
		private readonly Dictionary<string, string> _localVariablesMap = new Dictionary<string, string>();
		private IndentedStringWriter _writer = new IndentedStringWriter();
		private int _switchCount;
		private bool _insideSwitch;
		private string _switchExpression;
		private int _hebronTmpIndex = 0;

		private void ResetWriter() => _writer = new IndentedStringWriter();

		public void ConvertFunctions()
		{
			if (!Parameters.ConversionEntities.HasFlag(ConversionEntities.Functions))
			{
				return;
			}

			Logger.Info("Processing functions...");

			_state = State.Functions;
			foreach (var cursor in TranslationUnit.EnumerateCursors())
			{
				_localVariablesMap.Clear();
				ResetWriter();
				_hebronTmpIndex = 0;

				var funcDecl = cursor as FunctionDecl;
				if (funcDecl == null || !funcDecl.HasBody)
				{
					continue;
				}

				_functionDecl = funcDecl;

				var functionName = cursor.Spelling.FixSpecialWords();
				Logger.Info("Processing function {0}", functionName);

				if (Parameters.SkipFunctions.Contains(functionName))
				{
					Logger.Info("Skipped.");
					continue;
				}

				var name = cursor.Spelling.FixSpecialWords();

				_writer.IndentedWrite("pub unsafe fn " + name + "(");

				for(var i = 0; i < funcDecl.Parameters.Count; ++i)
				{
					var p = funcDecl.Parameters[i];
					var argName = p.Name.FixSpecialWords();
					var rustType = ToRustString(p.Type);

					_writer.Write("mut " + argName + ": " + rustType);
					if (i < funcDecl.Parameters.Count - 1)
					{
						_writer.Write(", ");
					}
				}

				_writer.Write(")");

				var returnTypeInfo = funcDecl.ReturnType.ToTypeInfo();
				if (!returnTypeInfo.IsVoid())
				{
					_writer.Write(" -> " + ToRustString(returnTypeInfo));
				}

				_writer.WriteLine(" {");

				++_writer.IndentLevel;

				foreach (var child in funcDecl.Body.Children)
				{
					var result = Process(child);
					if (result == null)
					{
						continue;
					}

					var stmt = result.Expression.EnsureStatementFinished();
					_writer.IndentedWriteLine(stmt);
				}

				--_writer.IndentLevel;
				_writer.IndentedWriteLine("}");


				Result.Functions[cursor.Spelling] = _writer.ToString();
			}
		}

		private string ProcessDeclaration(VarDecl info, out string name)
		{
			var isGlobalVariable = _state == State.GlobalVariables || info.StorageClass == CX_StorageClass.CX_SC_Static;

			string left, right;
			var size = info.CursorChildren.Count;
			name = info.Spelling.FixSpecialWords();

			if (_state == State.Functions && info.StorageClass == CX_StorageClass.CX_SC_Static)
			{
				name = _functionDecl.Spelling + "_" + name;
			}

			var typeInfo = info.Type.ToTypeInfo();

			var type = ToRustString(typeInfo);

			left = name + ": " + type;
			right = string.Empty;

			if (isGlobalVariable)
			{
				left = "pub static mut " + left;
			} else
			{
				left = "let mut " + left;
			}

			if (size > 0)
			{
				var rvalue = ProcessChildByIndex(info, size - 1);

				if (typeInfo.IsArray && rvalue.Info.CursorKind != CXCursorKind.CXCursor_InitListExpr)
				{
					// Array initializer
					var sb = new StringBuilder();
					sb.Append(new string('[', typeInfo.ConstantArraySizes.Length));
					sb.Append(typeInfo.TypeDescriptor.GetDefaltValue());
					for (var i = 0; i < typeInfo.ConstantArraySizes.Length; ++i)
					{
						sb.Append(";");
						sb.Append(typeInfo.ConstantArraySizes[i]);
						sb.Append("]");
					}

					right = sb.ToString();
				} else
				{
					right = rvalue.Expression;
				}
			}

			if (string.IsNullOrEmpty(right))
			{
				if (typeInfo.IsPointer)
				{
					right = NullPtr;
				}
				else
				{
					right = typeInfo.TypeDescriptor.GetDefaltValue();
				}
			}

			_currentStructInfo = null;

			var result = left;
			if (!string.IsNullOrEmpty(right))
			{
				result += " = " + right;
			}

			result = result.EnsureStatementEndWithSemicolon();

			return result;
		}

		internal void AppendNonZeroCheck(CursorProcessResult crp)
		{
			var info = crp.Info;

			if (info.CursorKind == CXCursorKind.CXCursor_BinaryOperator)
			{
				var type = clangsharp.Cursor_getBinaryOpcode(info.Handle);
				if (!type.IsBinaryOperator())
				{
					return;
				}
			}

			if (info.CursorKind == CXCursorKind.CXCursor_ParenExpr)
			{
				var child2 = ProcessChildByIndex(info, 0);
				if (child2.Info.CursorKind == CXCursorKind.CXCursor_BinaryOperator)
				{
					var type = clangsharp.Cursor_getBinaryOpcode(child2.Info.Handle);
					if (!type.IsBinaryOperator())
					{
						return;
					}
				}
			}

			if (info.CursorKind == CXCursorKind.CXCursor_UnaryOperator)
			{
				var child = ProcessChildByIndex(info, 0);
				var type = clangsharp.Cursor_getUnaryOpcode(info.Handle);
				if (child.IsPointer)
				{
					if (type == CX_UnaryOperatorKind.CX_UO_LNot)
					{
						crp.Expression = child.Expression + "== " + NullPtr;
					}

					return;
				}

				if (child.Info.CursorKind == CXCursorKind.CXCursor_ParenExpr)
				{
					var child2 = ProcessChildByIndex(child.Info, 0);
					if (child2.Info.CursorKind == CXCursorKind.CXCursor_BinaryOperator &&
						clangsharp.Cursor_getBinaryOpcode(child2.Info.Handle).IsBinaryOperator())
					{
					}
					else
					{
						return;
					}
				}

				if (type == CX_UnaryOperatorKind.CX_UO_LNot)
				{
					var sub = ProcessChildByIndex(crp.Info, 0);
					crp.Expression = sub.Expression + "== 0";

					return;
				}
			}

			if (crp.TypeInfo.TypeDescriptor is PrimitiveTypeInfo && !crp.IsPointer)
			{
				crp.Expression = crp.Expression.Parentize() + " != 0";
			}

			if (crp.IsPointer)
			{
				crp.Expression = crp.Expression.Parentize() + " != " + NullPtr;
			}
		}

		private CursorProcessResult ProcessChildByIndex(Cursor cursor, int index)
		{
			return Process(cursor.CursorChildren[index]);
		}

		private CursorProcessResult ProcessPossibleChildByIndex(Cursor cursor, int index)
		{
			if (cursor.CursorChildren.Count <= index)
			{
				return null;
			}

			return Process(cursor.CursorChildren[index]);
		}

		private bool AppendBoolToInt(Cursor info, ref string expression)
		{
			if (info.CursorKind == CXCursorKind.CXCursor_BinaryOperator &&
				clangsharp.Cursor_getBinaryOpcode(info.Handle).IsLogicalBooleanOperator())
			{
				expression = "if " + expression + "{ 1 } else { 0 }";
				return true;
			}
			else if (info.CursorKind == CXCursorKind.CXCursor_ParenExpr)
			{
				var child2 = ProcessPossibleChildByIndex(info, 0);
				if (child2 != null &&
					child2.Info.CursorKind == CXCursorKind.CXCursor_BinaryOperator &&
					clangsharp.Cursor_getBinaryOpcode(child2.Info.Handle).IsLogicalBooleanOperator())
				{
					expression = "if " + expression + "{ 1 } else { 0 }";
					return true;
				}
			}

			return false;
		}

		private string BuildUnaryOp(CursorProcessResult a, string name)
		{
			var method = "c_runtime::" + name;
			if (a.IsPointer) method += "Ptr";
			return method + "(& mut " + a.Expression + ")";
		}

		private string InternalProcess(Cursor info)
		{
			switch (info.Handle.Kind)
			{
				case CXCursorKind.CXCursor_EnumConstantDecl:
					{
						var expr = ProcessPossibleChildByIndex(info, 0);

						return info.Spelling + " = " + expr.Expression;
					}

				case CXCursorKind.CXCursor_UnaryExpr:
					{
						var opCode = clangsharp.Cursor_getUnaryOpcode(info.Handle);
						var expr = ProcessPossibleChildByIndex(info, 0);

						string[] tokens = null;
						if (opCode == CX_UnaryOperatorKind.CX_UO_Invalid && expr != null)
						{
							tokens = info.Tokenize();
							var op = "sizeof";
							if (tokens.Length > 0 && tokens[0] == "__alignof")
							{
								// 4 is default alignment
								return "4";
							}

							if (op == "sizeof" && !string.IsNullOrEmpty(expr.Expression))
							{
								if (expr.TypeInfo.ConstantArraySizes.Length > 1)
								{
									throw new Exception(string.Format("sizeof for arrays with {0} dimensions isn't supported.", 
										expr.TypeInfo.ConstantArraySizes.Length));
								}

								if (expr.TypeInfo.ConstantArraySizes.Length == 1)
								{
									return expr.TypeInfo.ConstantArraySizes[0] + " * " +ToRustTypeName(expr.TypeInfo).SizeOfExpr();
								}

								return expr.RustType.SizeOfExpr();
							}

							if (expr.Info.CursorKind == CXCursorKind.CXCursor_TypeRef)
							{
								return expr.RustType.SizeOfExpr();
							}
						}

						if (tokens == null)
						{
							tokens = info.Tokenize();
						}

						if (tokens.Length > 2 && tokens[0] == "sizeof")
						{
							return tokens[2].ToRustTypeName().SizeOfExpr();
						}

						var result = string.Join(string.Empty, tokens);
						return result;
					}
				case CXCursorKind.CXCursor_DeclRefExpr:
					{
						var name = info.Spelling.FixSpecialWords();
						if (_localVariablesMap.ContainsKey(name))
						{
							name = _localVariablesMap[name];
						}

						return name;
					}
				case CXCursorKind.CXCursor_CompoundAssignOperator:
				case CXCursorKind.CXCursor_BinaryOperator:
					{
						var a = ProcessChildByIndex(info, 0);
						var b = ProcessChildByIndex(info, 1);
						var type = clangsharp.Cursor_getBinaryOpcode(info.Handle);

						if (type.IsLogicalBinaryOperator())
						{
							AppendNonZeroCheck(a);
							AppendNonZeroCheck(b);
						}

						if (type == CX_BinaryOperatorKind.CX_BO_Assign)
						{
							// Check for multiple assigns per line
							if (b.Info.CursorKind == CXCursorKind.CXCursor_BinaryOperator)
							{
								var type2 = clangsharp.Cursor_getBinaryOpcode(b.Info.Handle);
								if (type2 == CX_BinaryOperatorKind.CX_BO_Assign)
								{
									var lvalues = new List<string>();

									lvalues.Add(a.Expression);

									// // Find right value
									while (type2 == CX_BinaryOperatorKind.CX_BO_Assign)
									{
										var a1 = ProcessChildByIndex(b.Info, 0);
										lvalues.Add(a1.Expression);

										b = ProcessChildByIndex(b.Info, 1);

										type2 = clangsharp.Cursor_getBinaryOpcode(b.Info.Handle);
									}

									var sb = new StringBuilder();

									var varName = "hebron_tmp" + _hebronTmpIndex;
									sb.Append("let " + varName + " = " + b.Expression.EnsureStatementFinished());
									foreach (var l in lvalues)
									{
										sb.Append(l + " = " + varName + ";");
									}

									++_hebronTmpIndex;

									return sb.ToString();
								}
							}
						}

						if (type.IsAssign() && type != CX_BinaryOperatorKind.CX_BO_ShlAssign && type != CX_BinaryOperatorKind.CX_BO_ShrAssign)
						{
							var typeInfo = info.ToTypeInfo();

							// Explicity cast right to left
							if (!typeInfo.IsPointer)
							{
								if (b.Info.CursorKind == CXCursorKind.CXCursor_ParenExpr && b.Info.CursorChildren.Count > 0)
								{
									var bb = ProcessChildByIndex(b.Info, 0);
									if (bb.Info.CursorKind == CXCursorKind.CXCursor_BinaryOperator &&
										clangsharp.Cursor_getBinaryOpcode(bb.Info.Handle).IsLogicalBooleanOperator())
									{
										b = bb;
									}
								}

								var expr = b.Expression;
								if (AppendBoolToInt(b.Info, ref expr))
								{
									b.Expression = expr;
								}
								else
								{
									b.Expression = b.Expression.Parentize();
								}

								b.Expression = b.Expression.ApplyCast(ToRustString(typeInfo)).EnsureStatementEndWithSemicolon();
							}
						}

						if (a.IsPointer && b.IsPrimitiveNumericType)
						{
							switch (type)
							{
								case CX_BinaryOperatorKind.CX_BO_Add:
									return "(" + a.Expression + ").offset((" + b.Expression + ") as isize)";
								case CX_BinaryOperatorKind.CX_BO_Sub:
									return "(" + a.Expression + ").offset(-((" + b.Expression + ") as isize))";
							}
						}

						if (a.IsPointer && (type == CX_BinaryOperatorKind.CX_BO_Assign || type.IsBooleanOperator()) &&
							(b.Expression.Deparentize() == "0"))
						{
							b.Expression = NullPtr;
						}

						if (a.IsPointer && b.IsPointer && type == CX_BinaryOperatorKind.CX_BO_Sub)
						{
							a.Expression = a.Expression.ApplyCast("usize");
							b.Expression = b.Expression.ApplyCast("usize");
						}

						if (a.IsPointer && type == CX_BinaryOperatorKind.CX_BO_AddAssign)
						{
							return a.Expression + " = " + a.Expression + ".offset((" + b.Expression + ") as isize)";
						}

						var str = info.GetOperatorString();

						var result = a.Expression + " " + str + " " + b.Expression;

						return result;
					}
				case CXCursorKind.CXCursor_UnaryOperator:
					{
						var a = ProcessChildByIndex(info, 0);

						var type = clangsharp.Cursor_getUnaryOpcode(info.Handle);
						var str = info.GetOperatorString();

						if (type == CX_UnaryOperatorKind.CX_UO_AddrOf)
						{
							str = "&mut ";
						}

						if (type == CX_UnaryOperatorKind.CX_UO_Deref)
						{
							str = "*";
						}

						if (type == CX_UnaryOperatorKind.CX_UO_PreInc)
						{
							return BuildUnaryOp(a, "preInc");
						}

						if (type == CX_UnaryOperatorKind.CX_UO_PostInc)
						{
							return BuildUnaryOp(a, "postInc");
						}

						if (type == CX_UnaryOperatorKind.CX_UO_PreDec)
						{
							return BuildUnaryOp(a, "preDec");
						}

						if (type == CX_UnaryOperatorKind.CX_UO_PostDec)
						{
							return BuildUnaryOp(a, "postDec");
						}

						if (type == CX_UnaryOperatorKind.CX_UO_Not)
						{
							str = "!";
						}

						var left = type.IsUnaryOperatorPre();
						if (left)
						{
							return str + a.Expression;
						}

						return a.Expression + str;
					}

				case CXCursorKind.CXCursor_CallExpr:
					{
						var size = info.CursorChildren.Count;

						var functionExpr = ProcessChildByIndex(info, 0);
						var functionName = functionExpr.Expression.Deparentize().UpdateNativeCall();

						// Retrieve arguments
						var args = new List<string>();
						for (var i = 1; i < size; ++i)
						{
							var argExpr = ProcessChildByIndex(info, i);

							var expr = argExpr.Expression;
							if (AppendBoolToInt(argExpr.Info, ref expr))
							{
								argExpr.Expression = expr;
							}
							else if (argExpr.IsPointer && argExpr.Expression.Deparentize() == "0")
							{
								argExpr.Expression = NullPtr;
							} else
							{
								var child = ProcessPossibleChildByIndex(argExpr.Info, 0);
								if (child != null)
								{
									if (argExpr.RustType != child.RustType)
									{
										argExpr.Expression = argExpr.Expression.ApplyCast(argExpr.RustType);
									} else
									{
										var subChild = ProcessPossibleChildByIndex(child.Info, 0);
										if (subChild != null && 
											subChild.Info.CursorKind == CXCursorKind.CXCursor_DeclRefExpr && 
											argExpr.RustType != subChild.RustType)
										{
											argExpr.Expression = argExpr.Expression.ApplyCast(argExpr.RustType);
										}
									}
								}
							}

							args.Add(argExpr.Expression);
						}

						var sb = new StringBuilder();

						sb.Append(functionName + "(");
						sb.Append(string.Join(", ", args));
						sb.Append(")");

						return sb.ToString();
					}
				case CXCursorKind.CXCursor_ReturnStmt:
					{
						var child = ProcessPossibleChildByIndex(info, 0);

						var ret = child.GetExpression();

						var tt = _functionDecl.ReturnType.ToTypeInfo();
						if (_functionDecl.ReturnType.Kind != CXTypeKind.CXType_Void)
						{
							if (!tt.IsPointer)
							{
								if (AppendBoolToInt(child.Info, ref ret))
								{
									return "return " + ret.ApplyCast("i32");
								}

								return "return " + ret.ApplyCast(ToRustString(tt));
							}
						}

						if (tt.IsPointer && ret.Deparentize() == "0")
						{
							ret = NullPtr;
						}

						var exp = string.IsNullOrEmpty(ret) ? "return" : "return " + ret;

						return exp;
					}
				case CXCursorKind.CXCursor_IfStmt:
					{
						var conditionExpr = ProcessChildByIndex(info, 0);
						AppendNonZeroCheck(conditionExpr);

						var executionExpr = ProcessChildByIndex(info, 1);
						var elseExpr = ProcessPossibleChildByIndex(info, 2);

						if (executionExpr != null && !string.IsNullOrEmpty(executionExpr.Expression))
						{
							executionExpr.Expression = executionExpr.Expression.EnsureStatementFinished().Curlize();
						}

						var expr = "if " + conditionExpr.Expression + " " + executionExpr.Expression;

						if (elseExpr != null)
						{
							expr += " else " + elseExpr.Expression.EnsureStatementFinished().Curlize();
						}

						return expr;
					}
				case CXCursorKind.CXCursor_ForStmt:
					{
						var size = info.CursorChildren.Count;

						CursorProcessResult execution = null, start = null, condition = null, it = null;
						switch (size)
						{
							case 1:
								execution = ProcessChildByIndex(info, 0);
								break;
							case 2:
								start = ProcessChildByIndex(info, 0);
								condition = ProcessChildByIndex(info, 1);
								break;
							case 3:
								var expr = ProcessChildByIndex(info, 0);
								if (expr.Info.CursorKind == CXCursorKind.CXCursor_BinaryOperator &&
									clangsharp.Cursor_getBinaryOpcode(expr.Info.Handle).IsBooleanOperator())
								{
									condition = expr;
								}
								else
								{
									start = expr;
								}

								expr = ProcessChildByIndex(info, 1);
								if (expr.Info.CursorKind == CXCursorKind.CXCursor_BinaryOperator &&
									clangsharp.Cursor_getBinaryOpcode(expr.Info.Handle).IsBooleanOperator())
								{
									condition = expr;
								}
								else
								{
									it = expr;
								}

								execution = ProcessChildByIndex(info, 2);
								break;
							case 4:
								start = ProcessChildByIndex(info, 0);
								condition = ProcessChildByIndex(info, 1);
								it = ProcessChildByIndex(info, 2);
								execution = ProcessChildByIndex(info, 3);
								break;
						}

						var executionExpr = ReplaceCommas(execution);
						executionExpr = executionExpr.EnsureStatementFinished();

						var startExpr = start.GetExpression().Replace(",", ";");
						var condExpr = condition.GetExpression();
						var itExpr = it.GetExpression().Replace(",", ";");

						if (string.IsNullOrEmpty(condExpr))
						{
							condExpr = "true";
						}

						if (execution.Info.CursorKind == CXCursorKind.CXCursor_CompoundStmt)
						{
							var openingBracketIndex = executionExpr.LastIndexOf('}');

							if (openingBracketIndex != -1)
							{
								executionExpr = executionExpr.Substring(0, openingBracketIndex) + itExpr.EnsureStatementFinished() + "}";

								return startExpr + ";" + Environment.NewLine + "while (" + condExpr + ") " + executionExpr;
							}
						}

						return startExpr + ";" + Environment.NewLine + "while (" + condExpr + ") {" +
							   executionExpr + itExpr.EnsureStatementFinished() + "}";
					}

				case CXCursorKind.CXCursor_CaseStmt:
					{
						var expr = ProcessChildByIndex(info, 0);
						var execution = ProcessChildByIndex(info, 1);
						var s2 = "if ";

						if (_switchCount > 0)
						{
							s2 = "} else " + s2;
						}

						++_switchCount;

						return s2 + _switchExpression + " == " + expr.Expression + " {" + execution.Expression;
					}

				case CXCursorKind.CXCursor_DefaultStmt:
					{
						var execution = ProcessChildByIndex(info, 0);

						var s2 = "else { ";
						if (_switchCount > 0)
						{
							s2 = "} " + s2;
						}

						++_switchCount;

						return s2 + execution.Expression;
					}

				case CXCursorKind.CXCursor_SwitchStmt:
					{
						_insideSwitch = true;
						_switchCount = 0;
						_switchExpression = ProcessChildByIndex(info, 0).Expression;
						var execution = ProcessChildByIndex(info, 1);

						_insideSwitch = false;
						return execution.Expression + "}";
					}

				case CXCursorKind.CXCursor_DoStmt:
					{
						var execution = ProcessChildByIndex(info, 0);
						var expr = ProcessChildByIndex(info, 1);

						AppendNonZeroCheck(expr);

						var exeuctionExpr = execution.Expression.EnsureStatementFinished();

						var breakExpr = "if !(" + expr.Expression + ") {break;}";

						if (execution.Info.CursorKind == CXCursorKind.CXCursor_CompoundStmt)
						{
							var closingBracketIndex = exeuctionExpr.LastIndexOf("}");
							if (closingBracketIndex != -1)
							{
								return "while(true) " + exeuctionExpr.Substring(0, closingBracketIndex) +
									breakExpr + exeuctionExpr.Substring(closingBracketIndex);
							}
						}

						return "while(true) {" + execution.Expression.EnsureStatementFinished() + breakExpr + "}";
					}

				case CXCursorKind.CXCursor_WhileStmt:
					{
						var expr = ProcessChildByIndex(info, 0);
						AppendNonZeroCheck(expr);
						var execution = ProcessChildByIndex(info, 1);

						return "while (" + expr.Expression + ") " + execution.Expression.EnsureStatementFinished().Curlize();
					}

				case CXCursorKind.CXCursor_LabelRef:
					return info.Spelling;
				case CXCursorKind.CXCursor_GotoStmt:
					{
						var label = ProcessChildByIndex(info, 0);

						return "goto " + label.Expression;
					}

				case CXCursorKind.CXCursor_LabelStmt:
					{
						var sb = new StringBuilder();

						sb.Append(info.Spelling);
						sb.Append(":;\n");

						var size = info.CursorChildren.Count;
						for (var i = 0; i < size; ++i)
						{
							var child = ProcessChildByIndex(info, i);
							sb.Append(child.Expression);
						}

						return sb.ToString();
					}

				case CXCursorKind.CXCursor_ConditionalOperator:
					{
						var condition = ProcessChildByIndex(info, 0);

						var a = ProcessChildByIndex(info, 1);
						var b = ProcessChildByIndex(info, 2);

						if (condition.TypeInfo.IsPrimitiveNumericType())
						{
							var gz = true;

							if (condition.Info.CursorKind == CXCursorKind.CXCursor_ParenExpr)
							{
								gz = false;
							}
							else if (condition.Info.CursorKind == CXCursorKind.CXCursor_BinaryOperator)
							{
								var op = clangsharp.Cursor_getBinaryOpcode(condition.Info.Handle);

								if (op == CX_BinaryOperatorKind.CX_BO_Or || op == CX_BinaryOperatorKind.CX_BO_And)
								{
								}
								else
								{
									gz = false;
								}
							}

							if (gz)
							{
								condition.Expression = condition.Expression.Parentize() + " != 0";
							}
						}

						return "if " + condition.Expression + "{" + a.Expression + "} else {" + b.Expression + "}";
					}

				case CXCursorKind.CXCursor_MemberRefExpr:
					{
						var a = ProcessChildByIndex(info, 0);

						var op = ".";

						if (a.Info.CursorKind == CXCursorKind.CXCursor_UnexposedExpr)
						{
							a.Expression = ("*" + a.Expression).Parentize();
						}

						var result = a.Expression + op + info.Spelling.FixSpecialWords();

						return result;
					}

				case CXCursorKind.CXCursor_IntegerLiteral:
					{
						var result = info.GetLiteralString();
						return result.Replace("U", string.Empty).Replace("u", string.Empty);
					}
				case CXCursorKind.CXCursor_FloatingLiteral:
					{
						var result = info.GetLiteralString();
						result += "32";

						return result;
					}
				case CXCursorKind.CXCursor_CharacterLiteral:
					{
						var r = info.GetLiteralString();
						if (string.IsNullOrEmpty(r))
						{
							return @"0";
						}

						return r.ToString();
					}

				case CXCursorKind.CXCursor_StringLiteral:
					return info.Spelling.StartsWith("L") ? info.Spelling.Substring(1) : info.Spelling;
				case CXCursorKind.CXCursor_VarDecl:
					{
						var varDecl = (VarDecl)info;
						string name;
						var expr = ProcessDeclaration(varDecl, out name);

						if (_state == State.Functions && 
							varDecl.StorageClass == CX_StorageClass.CX_SC_Static)
						{
							_localVariablesMap[varDecl.Spelling.FixSpecialWords()] = name;

							Result.GlobalVariables[name] = expr;
							return string.Empty;
						}

						return expr;
					}

				case CXCursorKind.CXCursor_DeclStmt:
					{
						var sb = new StringBuilder();
						var size = info.CursorChildren.Count;
						for (var i = 0; i < size; ++i)
						{
							var exp = ProcessChildByIndex(info, i);
							exp.Expression = exp.Expression.EnsureStatementFinished();
							sb.Append(exp.Expression);
						}

						return sb.ToString();
					}

				case CXCursorKind.CXCursor_CompoundStmt:
					{
						var sb = new StringBuilder();
						sb.Append("{" + Environment.NewLine);

						var size = info.CursorChildren.Count;
						for (var i = 0; i < size; ++i)
						{
							var exp = ProcessChildByIndex(info, i);
							exp.Expression = exp.Expression.EnsureStatementFinished();
							sb.Append(exp.Expression);
						}

						sb.Append("}" + Environment.NewLine);

						var fullExp = sb.ToString();

						return fullExp;
					}

				case CXCursorKind.CXCursor_ArraySubscriptExpr:
					{
						var var = ProcessChildByIndex(info, 0);
						var expr = ProcessChildByIndex(info, 1);

						if (!var.IsArray)
						{
							var child = ProcessPossibleChildByIndex(var.Info, 0);
							if (child == null ||
								(child != null && !child.IsArray))
							{
								return "*" + var.Expression + ".offset((" + expr.Expression + ") as isize)";
							}
						}

						return var.Expression + "[(" + expr.Expression + ") as usize]";
					}

				case CXCursorKind.CXCursor_InitListExpr:
					{
						var sb = new StringBuilder();

						var tt = info.ToTypeInfo();
						var initStruct = _currentStructInfo != null && !tt.IsArray;
						if (initStruct)
						{
							sb.Append("new " + ToRustTypeName(tt) + " ");
						}

						sb.Append("[ ");
						var size = info.CursorChildren.Count;
						for (var i = 0; i < size; ++i)
						{
							var exp = ProcessChildByIndex(info, i);

							if (initStruct)
							{
								if (i < _currentStructInfo.Count)
								{
									sb.Append(_currentStructInfo[i].Name + " = ");
								}
							}

							var val = exp.Expression;
							if (initStruct && i < _currentStructInfo.Count && _currentStructInfo[i].Type.Kind == CXTypeKind.CXType_Bool)
							{
								if (val == "0")
								{
									val = "false";
								}
								else if (val == "1")
								{
									val = "true";
								}
							}

							sb.Append(val);

							if (i < size - 1)
							{
								sb.Append(", ");
							}
						}

						sb.Append(" ]");
						return sb.ToString();
					}

				case CXCursorKind.CXCursor_ParenExpr:
					{
						var expr = ProcessPossibleChildByIndex(info, 0);
						var e = expr.GetExpression();
						var tt = info.ToTypeInfo();
						var rustType = ToRustString(tt);

						if (rustType != expr.RustType)
						{
							e = e.ApplyCast(rustType);
						} else
						{
							e = e.Parentize();
						}

						return e;
					}

				case CXCursorKind.CXCursor_BreakStmt:
					if (_insideSwitch)
					{
						return string.Empty;
					}
					return "break";
				case CXCursorKind.CXCursor_ContinueStmt:
					return "continue";

				case CXCursorKind.CXCursor_CStyleCastExpr:
					{
						var size = info.CursorChildren.Count;
						var child = ProcessChildByIndex(info, size - 1);

						var expr = child.Expression;
						var tt = info.ToTypeInfo();
						var rustType = ToRustString(tt);

						if (expr == "0" && tt.IsPointer)
						{
							// null
						} else if (rustType != child.RustType)
						{
							// cast
							expr = expr.ApplyCast(rustType);
						}

						return expr;
					}

				case CXCursorKind.CXCursor_UnexposedExpr:
					{
						// Return last child
						var size = info.CursorChildren.Count;

						if (size == 0)
						{
							return string.Empty;
						}

						var expr = ProcessChildByIndex(info, size - 1);
						var typeInfo = info.ToTypeInfo();
						var typeString = ToRustString(typeInfo);
						if (typeInfo.IsPointer && expr.Expression.Deparentize() == "0")
						{
							expr.Expression = NullPtr;
						}

						if (!typeInfo.IsArray &&
							typeInfo.IsPrimitiveNumericType() && 
							expr.IsPrimitiveNumericType &&
							typeString != expr.RustType &&
							expr.Info.CursorKind != CXCursorKind.CXCursor_StringLiteral &&
							expr.Info.CursorKind != CXCursorKind.CXCursor_ArraySubscriptExpr)
						{
							if (typeInfo.IsPointer && expr.IsArray)
							{
								expr.Expression = expr.Expression + ".as_mut_ptr()";
							}
							else
							{
								expr.Expression = expr.Expression.ApplyCast(typeString);
							}
						}

						return expr.Expression;
					}

				default:
					{
						// Return last child
						var size = info.CursorChildren.Count;

						if (size == 0)
						{
							return string.Empty;
						}

						var expr = ProcessPossibleChildByIndex(info, size - 1);

						return expr.GetExpression();
					}
			}
		}

		private CursorProcessResult Process(Cursor cursor)
		{
			var expr = InternalProcess(cursor);

			return new CursorProcessResult(this, cursor)
			{
				Expression = expr
			};
		}

		private string ReplaceCommas(CursorProcessResult info)
		{
			var executionExpr = info.GetExpression();
			if (info != null && info.Info.CursorKind == CXCursorKind.CXCursor_BinaryOperator)
			{
				var type = clangsharp.Cursor_getBinaryOpcode(info.Info.Handle);
				if (type == CX_BinaryOperatorKind.CX_BO_Comma)
				{
					var a = ReplaceCommas(ProcessChildByIndex(info.Info, 0));
					var b = ReplaceCommas(ProcessChildByIndex(info.Info, 1));

					executionExpr = a + ";" + b;
				}
			}

			return executionExpr;
		}
	}
}