/*

 Copyright (c) 2004-2006 Tomas Matousek, Vaclav Novak and Martin Maly.

 The use and distribution terms for this software are contained in the file named License.txt, 
 which can be found in the root of the Phalanger distribution. By using this software 
 in any fashion, you are agreeing to be bound by the terms of this license.
 
 You must not remove this notice from this software.

*/

using System;
using System.Reflection.Emit;
using System.Diagnostics;

using PHP.Core.Emit;
using PHP.Core.Parsers;
using PHP.Core.Reflection;

namespace PHP.Core.AST
{
	#region ConstantUse

	/// <summary>
	/// Base class for constant uses.
	/// </summary>
	public abstract class ConstantUse : Expression
	{
		protected DConstant constant;

		public ConstantUse(Position position)
			: base(position)
		{
		}

		internal abstract void ResolveName(Analyzer/*!*/ analyzer);

		/// <summary>
		/// Determines behavior on assignment.
		/// </summary>
		/// <include file='Doc/Nodes.xml' path='doc/method[@name="IsDeeplyCopied"]/*'/>
		/// <returns>Always <B>false</B>, since constants contain immutable objects only.</returns>
		internal override bool IsDeeplyCopied(CopyReason reason, int nestingLevel)
		{
			return false;
		}

		internal override Evaluation Analyze(Analyzer/*!*/ analyzer, ExInfoFromParent info)
		{
			bool already_resolved = constant != null;

			if (!already_resolved)
			{
				access = info.Access;
				ResolveName(analyzer);
			}

			if (constant.IsUnknown)
				return new Evaluation(this);

			KnownConstant known_const = (KnownConstant)constant;

			if (known_const.HasValue)
			{
				// constant value is known:
				return new Evaluation(this, known_const.Value);
			}
			else if (already_resolved)
			{
				// circular definition:
				constant.ReportCircularDefinition(analyzer.ErrorSink);
				return new Evaluation(this);
			}
			else
			{
				// value is not known yet, try to resolve it:
                if (known_const.Node != null)
				    known_const.Node.Analyze(analyzer);

				return (known_const.HasValue) ? new Evaluation(this, known_const.Value) : new Evaluation(this);
			}
		}

	}

	#endregion

	#region GlobalConstUse

	/// <summary>
	/// Global constant use (constants defined by <c>define</c> function).
	/// </summary>
	public sealed class GlobalConstUse : ConstantUse
	{
        public override Operations Operation { get { return Operations.GlobalConstUse; } }

		public QualifiedName Name { get { return name; } }
		private QualifiedName name;

        /// <summary>
        /// Name used when the <see cref="Name"/> is not found. Used when reading global constant in a namespace context.
        /// </summary>
        private QualifiedName? fallbackName;

		public GlobalConstUse(Position position, QualifiedName name, QualifiedName? fallbackName)
			: base(position)
		{
			this.name = name;
            this.fallbackName = fallbackName;
		}

		/// <summary>
		/// Called only by Analyzer. O this instance analyze method will not be called.
		/// </summary>
		internal GlobalConstUse(Position position, QualifiedName name, AccessType access)
			: base(position)
		{
			this.access = access;
			this.name = name;
            this.fallbackName = null;
		}

		internal override Evaluation EvaluatePriorAnalysis(SourceUnit/*!*/ sourceUnit)
		{
			constant = sourceUnit.TryResolveGlobalConstantGlobally(name);
			return (constant != null && constant.HasValue) ? new Evaluation(this, constant.Value) : new Evaluation(this);
		}

		internal override void ResolveName(Analyzer/*!*/ analyzer)
		{
			if (constant == null)
                constant = analyzer.ResolveGlobalConstantName(name, this.Position);
		}

		/// <include file='Doc/Nodes.xml' path='doc/method[@name="Emit"]/*'/>
		/// <remarks>
		/// Emits IL instructions to load the value of the constant. If the value is known at compile 
		/// time (constant is system), its value is loaded on the stack. Otherwise the value is 
		/// obtained at runtime by calling <see cref="PHP.Core.ScriptContext.GetConstantValue"/>.
		/// </remarks>
		internal override PhpTypeCode Emit(CodeGenerator/*!*/ codeGenerator)
		{
			Debug.Assert(access == AccessType.Read || access == AccessType.None);
			Statistics.AST.AddNode("ConstantUse.Global");

			// loads constant only if its value is read:
			if (access == AccessType.Read)
			{
                return constant.EmitGet(codeGenerator, null, false, fallbackName.HasValue ? fallbackName.Value.ToString() : null);
			}
			else
			{
				// to satisfy debugger; sequence point has already been defined:
				if (codeGenerator.Context.Config.Compiler.Debug)
					codeGenerator.IL.Emit(OpCodes.Nop);
			}

			return PhpTypeCode.Void;
		}

        /// <summary>
        /// Call the right Visit* method on the given Visitor object.
        /// </summary>
        /// <param name="visitor">Visitor to be called.</param>
        public override void VisitMe(TreeVisitor visitor)
        {
            visitor.VisitGlobalConstUse(this);
        }
	}

	#endregion

	#region ClassConstUse

	/// <summary>
	/// Class constant use.
	/// </summary>
	public class ClassConstUse : ConstantUse
	{
		public override Operations Operation { get { return Operations.ClassConstUse; } }

        /// <summary>
        /// Class name. May have an empty <see cref="Name"/> if the class is referenced indirectly.
        /// </summary>
        public GenericQualifiedName ClassName { get { return this.typeRef.GenericQualifiedName; } }

        /// <summary>
        /// Class type reference.
        /// </summary>
        public TypeRef/*!*/TypeRef { get { return this.typeRef; } }
        private readonly TypeRef/*!*/typeRef;
        protected DType/*!A*/type;

		public VariableName Name { get { return name; } }
		private readonly VariableName name;

        /// <summary>
        /// Position of <see cref="Name"/> part of the constant use.
        /// </summary>
        public Position NamePosition { get; private set; }

		bool runtimeVisibilityCheck;

        public ClassConstUse(Position position, GenericQualifiedName className, Position classNamePosition, string/*!*/ name, Position namePosition)
            : this(position, DirectTypeRef.FromGenericQualifiedName(classNamePosition, className), name, namePosition)
		{
		}

        public ClassConstUse(Position position, TypeRef/*!*/typeRef, string/*!*/ name, Position namePosition)
            : base(position)
        {
            Debug.Assert(typeRef != null);
            Debug.Assert(!string.IsNullOrEmpty(name));

            this.typeRef = typeRef;
			this.name = new VariableName(name);
            this.NamePosition = namePosition;
        }

		internal override Evaluation EvaluatePriorAnalysis(SourceUnit/*!*/ sourceUnit)
		{
            var className = this.ClassName;
            if (!string.IsNullOrEmpty(className.QualifiedName.Name.Value))
                constant = sourceUnit.TryResolveClassConstantGlobally(className, name);

			return (constant != null && constant.HasValue) ? new Evaluation(this, constant.Value) : new Evaluation(this);
		}

		internal override void ResolveName(Analyzer/*!*/ analyzer)
		{
            this.typeRef.Analyze(analyzer);
            this.type = this.typeRef.ResolvedTypeOrUnknown;

            // analyze constructed type (we are in the full analysis):
			analyzer.AnalyzeConstructedType(type);

            constant = analyzer.ResolveClassConstantName(type, name, this.Position, analyzer.CurrentType, analyzer.CurrentRoutine,
				  out runtimeVisibilityCheck);
		}

		/// <include file='Doc/Nodes.xml' path='doc/method[@name="Emit"]/*'/>
		internal override PhpTypeCode Emit(CodeGenerator/*!*/ codeGenerator)
		{
			Debug.Assert(access == AccessType.None || access == AccessType.Read);
			Statistics.AST.AddNode("ConstantUse.Class");

			if (access == AccessType.Read)
			{
                return constant.EmitGet(codeGenerator, type as ConstructedType, runtimeVisibilityCheck, null);
			}
			else
			{
				// to satisfy debugger; sequence point has already been defined:
				if (codeGenerator.Context.Config.Compiler.Debug)
					codeGenerator.IL.Emit(OpCodes.Nop);
			}

			return PhpTypeCode.Void;
		}

        /// <summary>
        /// Call the right Visit* method on the given Visitor object.
        /// </summary>
        /// <param name="visitor">Visitor to be called.</param>
        public override void VisitMe(TreeVisitor visitor)
        {
            visitor.VisitClassConstUse(this);
        }
	}

    /// <summary>
    /// Pseudo class constant use.
    /// </summary>
    public sealed class PseudoClassConstUse : ClassConstUse
    {
        /// <summary>
        /// Possible types of pseudo class constant.
        /// </summary>
        public enum Types
        {
            Class
        }

        /// <summary>Type of pseudoconstant</summary>
        public Types Type { get { return consttype; } }
        private Types consttype;
        
        public PseudoClassConstUse(Position position, GenericQualifiedName className, Position classNamePosition, Types type, Position namePosition)
            : this(position, DirectTypeRef.FromGenericQualifiedName(classNamePosition, className), type, namePosition)
		{
		}

        public PseudoClassConstUse(Position position, TypeRef/*!*/typeRef, Types type, Position namePosition)
            : base(position, typeRef, type.ToString().ToLowerInvariant(), namePosition)
        {
            this.consttype = type;
        }

        public override object Value
        {
            get
            {
                switch (this.consttype)
                {
                    case Types.Class:
                        var className = this.ClassName;
                        if (string.IsNullOrEmpty(className.QualifiedName.Name.Value) ||
                            className.QualifiedName.IsStaticClassName ||
                            className.QualifiedName.IsSelfClassName)
                            return null;

                        return className.QualifiedName.ToString();

                    default:
                        throw new InvalidOperationException();
                }
            }
        }

        internal override void ResolveName(Analyzer/*!*/ analyzer)
        {
            if (this.type != null)
                return;

            this.TypeRef.Analyze(analyzer);
            this.type = this.TypeRef.ResolvedTypeOrUnknown;

            // analyze constructed type (we are in the full analysis):
            analyzer.AnalyzeConstructedType(type);

            //
            this.constant = null;
        }

        internal override Evaluation EvaluatePriorAnalysis(SourceUnit sourceUnit)
        {
            var value = this.Value;
            if (value != null)
                return new Evaluation(this, value);
            else
                return base.EvaluatePriorAnalysis(sourceUnit);
        }

        internal override Evaluation Analyze(Analyzer analyzer, ExInfoFromParent info)
        {
            access = info.access;

            var value = this.Value;
            if (value != null)
                return new Evaluation(this, value);
            
            //
            this.ResolveName(analyzer);

            //
            return new Evaluation(this);
        }

        internal override PhpTypeCode Emit(CodeGenerator codeGenerator)
        {
            switch (this.consttype)
            {
                case Types.Class:
                    this.type.EmitLoadTypeDesc(codeGenerator, ResolveTypeFlags.ThrowErrors | ResolveTypeFlags.UseAutoload);
                    codeGenerator.IL.Emit(OpCodes.Call, Methods.Operators.GetFullyQualifiedName);
                    return PhpTypeCode.String;

                default:
                    throw new InvalidOperationException();
            }
        }

        public override void VisitMe(TreeVisitor visitor)
        {
            base.VisitMe(visitor);
        }
    }

	#endregion

    #region PseudoConstUse

    /// <summary>
	/// Pseudo-constant use (PHP keywords: __LINE__, __FILE__, __DIR__, __FUNCTION__, __METHOD__, __CLASS__, __NAMESPACE__)
	/// </summary>
	public sealed class PseudoConstUse : Expression
	{
        public override Operations Operation { get { return Operations.PseudoConstUse; } }

		public enum Types { Line, File, Class, Function, Method, Namespace, Dir }

		private Types type;
        /// <summary>Type of pseudoconstant</summary>
        public Types Type { get { return type; } }

		public PseudoConstUse(Position position, Types type)
			: base(position)
		{
			this.type = type;
		}

		#region Analysis

        /// <summary>
        /// Get the value indicating if the given constant is evaluable in compile time.
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        private bool IsEvaluable(Types type)
        {
            switch (type)
            {
                case Types.File:
                case Types.Dir:
                    return false;
                default:
                    return true;
            }
        }

		/// <include file='Doc/Nodes.xml' path='doc/method[@name="Expression.Analyze"]/*'/>
		internal override Evaluation Analyze(Analyzer/*!*/ analyzer, ExInfoFromParent info)
		{
			access = info.access;

            if (IsEvaluable(type))
				return new Evaluation(this, Evaluate(analyzer));
			else
				return new Evaluation(this);
		}

		/// <summary>
		/// Gets value of __LINE__, __FUNCTION__, __METHOD__, __CLASS__, __NAMESPACE__ used in source code.
		/// Doesn't get value for __FILE__ and __DIR__. This value is combined from relative path and the current source root
		/// at run-time.
		/// </summary>
		/// <remarks>
		/// Analyzer maintains during AST walk information about its position in AST
		/// and that information uses (among others) to provide values of the pseudo constants.
		/// </remarks>
		private object Evaluate(Analyzer/*!*/ analyzer)
		{
			// TODO: sync with PHP behavior
			switch (type)
			{
				case Types.Line:
                    return (int)this.Position.FirstLine; // __LINE__ is of type Integer in PHP

				case Types.Class:
					if (analyzer.CurrentType != null)
						return analyzer.CurrentType.FullName;

					return string.Empty;

				case Types.Function:
					if (analyzer.CurrentRoutine != null)
						return analyzer.CurrentRoutine.FullName;

                    return string.Empty;

				case Types.Method:
					if (analyzer.CurrentRoutine != null)
					{
						if (analyzer.CurrentRoutine.IsMethod)
						{
							return ((KnownType)analyzer.CurrentRoutine.DeclaringType).QualifiedName.ToString(
							  ((PhpMethod)analyzer.CurrentRoutine).Name, false);
						}
						else
							return analyzer.CurrentRoutine.FullName;
					}
					return string.Empty;

				case Types.Namespace:
                    return analyzer.CurrentNamespace.HasValue ? analyzer.CurrentNamespace.Value.NamespacePhpName : string.Empty;

				case Types.File:
                case Types.Dir:
					Debug.Fail("Evaluated at run-time.");
					return null;

				default:
					throw null;
			}
		}

		#endregion

        /// <summary>
        /// Emit
        /// CALL Operators.ToAbsoluteSourcePath(relative source path level, remaining relative source path);
        /// </summary>
        /// <param name="codeGenerator">Code generator.</param>
        /// <returns>Type code of value that is on the top of the evaluation stack as the result of call of emitted code.</returns>
        private PhpTypeCode EmitToAbsoluteSourcePath(CodeGenerator/*!*/ codeGenerator)
        {
            ILEmitter il = codeGenerator.IL;

            // CALL Operators.ToAbsoluteSourcePath(<relative source path level>, <remaining relative source path>);
            RelativePath relative_path = codeGenerator.SourceUnit.SourceFile.RelativePath;
            il.LdcI4(relative_path.Level);
            il.Emit(OpCodes.Ldstr, relative_path.Path);
            il.Emit(OpCodes.Call, Methods.Operators.ToAbsoluteSourcePath);

            //
            return PhpTypeCode.String;
        }

		/// <include file='Doc/Nodes.xml' path='doc/method[@name="Emit"]/*'/>
		internal override PhpTypeCode Emit(CodeGenerator/*!*/ codeGenerator)
		{
			if (type == Types.File)
            {
                EmitToAbsoluteSourcePath(codeGenerator);
            }
            else if (type == Types.Dir)
            {
                ILEmitter il = codeGenerator.IL;

                // CALL Path.GetDirectory( Operators.ToAbsoluteSourcePath(...) )
                EmitToAbsoluteSourcePath(codeGenerator);
                il.Emit(OpCodes.Call, Methods.Path.GetDirectoryName);
            }
            else
            {
                Debug.Fail("Pseudo constant " + type.ToString() + " expected to be already evaluated.");
            }

			return PhpTypeCode.String;
		}

        /// <summary>
        /// Call the right Visit* method on the given Visitor object.
        /// </summary>
        /// <param name="visitor">Visitor to be called.</param>
        public override void VisitMe(TreeVisitor visitor)
        {
            visitor.VisitPseudoConstUse(this);
        }
	}

	#endregion
}
