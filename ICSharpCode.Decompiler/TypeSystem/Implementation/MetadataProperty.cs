﻿// Copyright (c) 2018 Daniel Grunwald
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using ICSharpCode.Decompiler.Util;

namespace ICSharpCode.Decompiler.TypeSystem.Implementation
{
	sealed class MetadataProperty : IProperty
	{
		const Accessibility InvalidAccessibility = (Accessibility)0xff;

		readonly MetadataModule module;
		readonly PropertyDefinitionHandle propertyHandle;
		readonly IMethod getter;
		readonly IMethod setter;
		readonly string name;
		readonly SymbolKind symbolKind;

		// lazy-loaded:
		volatile Accessibility cachedAccessiblity = InvalidAccessibility;
		IParameter[] parameters;
		IType returnType;

		internal MetadataProperty(MetadataModule module, PropertyDefinitionHandle handle)
		{
			Debug.Assert(module != null);
			Debug.Assert(!handle.IsNil);
			this.module = module;
			this.propertyHandle = handle;

			var metadata = module.metadata;
			var prop = metadata.GetPropertyDefinition(handle);
			var accessors = prop.GetAccessors();
			getter = module.GetDefinition(accessors.Getter);
			setter = module.GetDefinition(accessors.Setter);
			name = metadata.GetString(prop.Name);
			// Maybe we should defer the calculation of symbolKind?
			if (DetermineIsIndexer(name)) {
				symbolKind = SymbolKind.Indexer;
			} else if (name.IndexOf('.') >= 0) {
				// explicit interface implementation
				var interfaceProp = this.ExplicitlyImplementedInterfaceMembers.FirstOrDefault() as IProperty;
				symbolKind = interfaceProp?.SymbolKind ?? SymbolKind.Property;
			} else {
				symbolKind = SymbolKind.Property;
			}
		}

		bool DetermineIsIndexer(string name)
		{
			if (name != (DeclaringTypeDefinition as MetadataTypeDefinition)?.DefaultMemberName)
				return false;
			return Parameters.Count > 0;
		}

		public override string ToString()
		{
			return $"{MetadataTokens.GetToken(propertyHandle):X8} {DeclaringType?.ReflectionName}.{Name}";
		}

		public EntityHandle MetadataToken => propertyHandle;
		public string Name => name;

		public bool CanGet => getter != null;
		public bool CanSet => setter != null;

		public IMethod Getter => getter;
		public IMethod Setter => setter;
		IMethod AnyAccessor => getter ?? setter;

		public bool IsIndexer => symbolKind == SymbolKind.Indexer;
		public SymbolKind SymbolKind => symbolKind;

		#region Signature (ReturnType + Parameters)
		public IReadOnlyList<IParameter> Parameters {
			get {
				var parameters = LazyInit.VolatileRead(ref this.parameters);
				if (parameters != null)
					return parameters;
				DecodeSignature();
				return this.parameters;
			}
		}

		public IType ReturnType {
			get {
				var returnType = LazyInit.VolatileRead(ref this.returnType);
				if (returnType != null)
					return returnType;
				DecodeSignature();
				return this.returnType;
			}
		}

		private void DecodeSignature()
		{
			var propertyDef = module.metadata.GetPropertyDefinition(propertyHandle);
			var genericContext = new GenericContext(DeclaringType.TypeParameters);
			var signature = propertyDef.DecodeSignature(module.TypeProvider, genericContext);
			var accessors = propertyDef.GetAccessors();
			ParameterHandleCollection? parameterHandles;
			if (!accessors.Getter.IsNil)
				parameterHandles = module.metadata.GetMethodDefinition(accessors.Getter).GetParameters();
			else if (!accessors.Setter.IsNil)
				parameterHandles = module.metadata.GetMethodDefinition(accessors.Setter).GetParameters();
			else
				parameterHandles = null;
			var (returnType, parameters) = MetadataMethod.DecodeSignature(module, this, signature, parameterHandles);
			LazyInit.GetOrSet(ref this.returnType, returnType);
			LazyInit.GetOrSet(ref this.parameters, parameters);
		}
		#endregion

		public bool IsExplicitInterfaceImplementation => AnyAccessor?.IsExplicitInterfaceImplementation ?? false;
		public IEnumerable<IMember> ExplicitlyImplementedInterfaceMembers => GetInterfaceMembersFromAccessor(AnyAccessor);

		internal static IEnumerable<IMember> GetInterfaceMembersFromAccessor(IMethod method)
		{
			if (method == null)
				return EmptyList<IMember>.Instance;
			return method.ExplicitlyImplementedInterfaceMembers.Select(m => ((IMethod)m).AccessorOwner).Where(m => m != null);
		}

		public ITypeDefinition DeclaringTypeDefinition => AnyAccessor?.DeclaringTypeDefinition;
		public IType DeclaringType => AnyAccessor?.DeclaringType;
		IMember IMember.MemberDefinition => this;
		TypeParameterSubstitution IMember.Substitution => TypeParameterSubstitution.Identity;

		#region Attributes
		public IEnumerable<IAttribute> GetAttributes()
		{
			var b = new AttributeListBuilder(module);
			var metadata = module.metadata;
			var propertyDef = metadata.GetPropertyDefinition(propertyHandle);
			if (IsIndexer && Name != "Item" && !IsExplicitInterfaceImplementation) {
				b.Add(KnownAttribute.IndexerName, KnownTypeCode.String, Name);
			}
			b.Add(propertyDef.GetCustomAttributes());
			return b.Build();
		}
		#endregion

		#region Accessibility
		public Accessibility Accessibility {
			get {
				var acc = cachedAccessiblity;
				if (acc == InvalidAccessibility)
					return cachedAccessiblity = ComputeAccessibility();
				else
					return acc;
			}
		}

		Accessibility ComputeAccessibility()
		{
			if (IsOverride && (getter == null || setter == null)) {
				foreach (var baseMember in InheritanceHelper.GetBaseMembers(this, includeImplementedInterfaces: false)) {
					if (!baseMember.IsOverride)
						return baseMember.Accessibility;
				}
			}
			return MergePropertyAccessibility(
				this.Getter?.Accessibility ?? Accessibility.None,
				this.Setter?.Accessibility ?? Accessibility.None);
		}

		static internal Accessibility MergePropertyAccessibility(Accessibility left, Accessibility right)
		{
			if (left == Accessibility.Public || right == Accessibility.Public)
				return Accessibility.Public;

			if (left == Accessibility.ProtectedOrInternal || right == Accessibility.ProtectedOrInternal)
				return Accessibility.ProtectedOrInternal;

			if (left == Accessibility.Protected && right == Accessibility.Internal ||
				left == Accessibility.Internal && right == Accessibility.Protected)
				return Accessibility.ProtectedOrInternal;

			if (left == Accessibility.Protected || right == Accessibility.Protected)
				return Accessibility.Protected;

			if (left == Accessibility.Internal || right == Accessibility.Internal)
				return Accessibility.Internal;

			if (left == Accessibility.ProtectedAndInternal || right == Accessibility.ProtectedAndInternal)
				return Accessibility.ProtectedAndInternal;

			if (left == Accessibility.Private || right == Accessibility.Private)
				return Accessibility.Private;

			return left;
		}
		#endregion

		public bool IsStatic => AnyAccessor?.IsStatic ?? false;
		public bool IsAbstract => AnyAccessor?.IsAbstract ?? false;
		public bool IsSealed => AnyAccessor?.IsSealed ?? false;
		public bool IsVirtual => AnyAccessor?.IsVirtual ?? false;
		public bool IsOverride => AnyAccessor?.IsOverride ?? false;
		public bool IsOverridable => AnyAccessor?.IsOverridable ?? false;

		public IModule ParentModule => module;
		public ICompilation Compilation => module.Compilation;

		public string FullName => $"{DeclaringType?.FullName}.{Name}";
		public string ReflectionName => $"{DeclaringType?.ReflectionName}.{Name}";
		public string Namespace => DeclaringType?.Namespace ?? string.Empty;

		public override bool Equals(object obj)
		{
			if (obj is MetadataProperty p) {
				return propertyHandle == p.propertyHandle && module.PEFile == p.module.PEFile;
			}
			return false;
		}

		public override int GetHashCode()
		{
			return 0x32b6a76c ^ module.PEFile.GetHashCode() ^ propertyHandle.GetHashCode();
		}

		bool IMember.Equals(IMember obj, TypeVisitor typeNormalization)
		{
			return Equals(obj);
		}

		public IMember Specialize(TypeParameterSubstitution substitution)
		{
			return SpecializedProperty.Create(this, substitution);
		}
	}
}
