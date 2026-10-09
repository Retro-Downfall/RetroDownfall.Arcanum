using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using Microsoft.CodeAnalysis.Operations;

using System.Security.AccessControl;

using System.Security.Principal;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        private bool IsReviewedCampaignAclMethod(
            IMethodSymbol method,
            SyntaxNode? node,
            SemanticModel? model,
            AuthoredMember? context)
        {
            if (node is null || model is null)
            {
                return false;
            }

            if (method.MethodKind == MethodKind.Constructor)
            {
                return (TypeKey(method.ContainingType) is "System.Security.AccessControl.DirectorySecurity"
                        or "System.Security.AccessControl.FileSystemAccessRule")
                    && model.GetOperation(node) is IObjectCreationOperation creation
                    && (IsExactCampaignDirectorySecurityConstruction(creation)
                        || IsExactCampaignAccessRuleConstruction(creation));
            }

            if (TypeKey(method.ContainingType) is not "System.Security.AccessControl.ObjectSecurity"
                and not "System.Security.AccessControl.FileSystemSecurity")
            {
                return false;
            }

            bool objectSecurity = IsExactFrameworkType(method.ContainingType,
                "System.Security.AccessControl.ObjectSecurity", typeof(ObjectSecurity).Assembly.GetName());

            bool setOwner = objectSecurity
                && method.Name == "SetOwner"
                && HasExactParameterTypes(method, "System.Security.Principal.IdentityReference");

            bool protectRules = objectSecurity
                && method.Name == "SetAccessRuleProtection"
                && HasExactParameterTypes(method, "System.Boolean", "System.Boolean");

            bool addRule = method.Name == "AddAccessRule"
                && IsExactFrameworkType(method.ContainingType,
                    "System.Security.AccessControl.FileSystemSecurity", typeof(FileSystemSecurity).Assembly.GetName())
                && HasExactParameterTypes(method, "System.Security.AccessControl.FileSystemAccessRule");

            if (method.IsStatic
                || method.Arity != 0
                || !method.ReturnsVoid
                || !setOwner && !protectRules && !addRule
                || model.GetOperation(node) is not IInvocationOperation invocation
                || invocation.Instance is not { Syntax: ExpressionSyntax receiver } receiverOperation
                || HasCampaignAuthoredValueConversion(receiverOperation)
                || !HasFreshCampaignDirectorySecurityProvenance(
                    receiver,
                    model,
                    new HashSet<ISymbol>(SymbolEqualityComparer.Default),
                    context))
            {
                return false;
            }

            if (setOwner)
            {
                // ObjectSecurity.SetOwner calls the identity's virtual Translate method.
                // The pinned sealed SID translates to its own type by returning itself.
                return invocation.Arguments is [IArgumentOperation owner]
                    && IsExactCampaignSecurityIdentifier(owner.Value);
            }

            if (protectRules)
            {
                return HasCampaignAclConstantArgument(invocation.Arguments, 0, true)
                    && HasCampaignAclConstantArgument(invocation.Arguments, 1, false);
            }

            return invocation.Arguments is [IArgumentOperation { Value.Syntax: ExpressionSyntax rule } ruleArgument]
                && !HasCampaignAuthoredValueConversion(ruleArgument.Value)
                && HasFreshCampaignAccessRuleProvenance(
                    rule,
                    model,
                    new HashSet<ISymbol>(SymbolEqualityComparer.Default),
                    context);
        }

        private static bool IsExactCampaignDirectorySecurityConstruction(IObjectCreationOperation creation) =>
            creation.Constructor is { MethodKind: MethodKind.Constructor, Parameters.Length: 0 } constructor
            && constructor.ContainingType.IsSealed
            && IsExactFrameworkType(constructor.ContainingType,
                "System.Security.AccessControl.DirectorySecurity", typeof(DirectorySecurity).Assembly.GetName());

        private static bool IsExactCampaignAccessRuleConstruction(IObjectCreationOperation creation)
        {
            if (creation.Constructor is not { MethodKind: MethodKind.Constructor } constructor
                || !constructor.ContainingType.IsSealed
                || !IsExactFrameworkType(constructor.ContainingType,
                    "System.Security.AccessControl.FileSystemAccessRule", typeof(FileSystemAccessRule).Assembly.GetName())
                || !HasExactParameterTypes(constructor,
                    "System.Security.Principal.IdentityReference",
                    "System.Security.AccessControl.FileSystemRights",
                    "System.Security.AccessControl.InheritanceFlags",
                    "System.Security.AccessControl.PropagationFlags",
                    "System.Security.AccessControl.AccessControlType")
                || creation.Arguments.SingleOrDefault(static argument => argument.Parameter?.Ordinal == 0)
                    is not { Value: { } identity }
                || !IsExactCampaignSecurityIdentifier(identity))
            {
                return false;
            }

            // Runtime v10.0.12 FileSystemAccessRule -> AuthorizationRule invokes
            // IdentityReference.IsValidTargetType; an opaque identity is a callback boundary.
            // Compare the pinned enum metadata values without invoking Windows-only APIs:
            // FullControl=0x001F01FF, ContainerInherit=1, ObjectInherit=2, None=0, Allow=0.
            return HasCampaignAclConstantArgument(creation.Arguments, 1, 0x001F01FF)
                && HasCampaignAclConstantArgument(creation.Arguments, 2, 3)
                && HasCampaignAclConstantArgument(creation.Arguments, 3, 0)
                && HasCampaignAclConstantArgument(creation.Arguments, 4, 0);
        }

        private static bool IsExactCampaignSecurityIdentifier(IOperation value)
        {
            if (HasCampaignAuthoredValueConversion(value))
            {
                return false;
            }

            while (value is IConversionOperation conversion)
            {
                value = conversion.Operand;
            }

            return value.Type is INamedTypeSymbol { IsSealed: true } identity
                && IsExactFrameworkType(identity,
                    "System.Security.Principal.SecurityIdentifier", typeof(SecurityIdentifier).Assembly.GetName());
        }

        private bool HasFreshCampaignDirectorySecurityProvenance(
            ExpressionSyntax expression,
            SemanticModel model,
            HashSet<ISymbol> path,
            AuthoredMember? context)
        {
            if (!TryStripCampaignEncodingValue(expression, model, out expression))
            {
                return false;
            }

            // The parameterless sealed DirectorySecurity constructor follows only the
            // in-memory NativeObjectSecurity/CommonObjectSecurity descriptor constructors.
            // The name/handle constructors instead call Win32.GetSecurityInfo.
            if (model.GetOperation(expression) is IObjectCreationOperation creation)
            {
                return IsExactCampaignDirectorySecurityConstruction(creation);
            }

            return HasOnlyReviewedValueSources(
                expression,
                model,
                path,
                context,
                HasFreshCampaignDirectorySecurityProvenance);
        }

        private bool HasFreshCampaignAccessRuleProvenance(
            ExpressionSyntax expression,
            SemanticModel model,
            HashSet<ISymbol> path,
            AuthoredMember? context)
        {
            if (!TryStripCampaignEncodingValue(expression, model, out expression))
            {
                return false;
            }

            // AddAccessRule dispatches ModifyAccess and translates the rule's stored identity.
            // Exact sealed DirectorySecurity plus a sealed SID-based rule closes both targets.
            // FileSystemAclExtensions.Create and Persist remain filesystem effects.
            if (model.GetOperation(expression) is IObjectCreationOperation creation)
            {
                return IsExactCampaignAccessRuleConstruction(creation);
            }

            return HasOnlyReviewedValueSources(
                expression,
                model,
                path,
                context,
                HasFreshCampaignAccessRuleProvenance);
        }

        private static bool HasCampaignAclConstantArgument(
            IEnumerable<IArgumentOperation> arguments,
            int ordinal,
            object expected) =>
            arguments.SingleOrDefault(argument => argument.Parameter?.Ordinal == ordinal)?.Value.ConstantValue
                is { HasValue: true, Value: { } value }
            && value.Equals(expected);
    }
}
