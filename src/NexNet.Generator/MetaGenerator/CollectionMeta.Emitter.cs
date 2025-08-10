using System.Text;

namespace NexNet.Generator.MetaGenerator;
partial class CollectionMeta
{
    /// <summary>
    /// Emit the code for the the nexus.
    /// </summary>
    /// <param name="sb"></param>
    public void EmitNexusInvocation(StringBuilder sb)
    {
        sb.Append(@"
                        var arguments = message.DeserializeArguments<global::System.ValueTuple<global::System.Byte>>();
                        duplexPipe = await methodInvoker.RegisterDuplexPipe(arguments.Item1).ConfigureAwait(false);
                        this.Context.Logger?.NexusLog($""Nexus ").Append(CollectionTypeShortString).Append(" Collection connection Invocation: ").Append(Name).Append(@" pipe = {arguments.Item1}"");
    
                        await global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.ICollectionStore>(this).StartCollection(").Append(Id).AppendLine(", duplexPipe);");
    }
    
    public void EmitCollectionConfigure(StringBuilder sb)
    {
        sb.Append("            manager.ConfigureList<").Append(this.ReturnTypeArity).Append(">(").Append(Id).Append(", ").Append(CollectionModeFullTypeString).AppendLine(");");
    }
    

    public void EmitProxyAccessor(StringBuilder sb)
    {
        sb.Append("            public ");
        sb.Append(CollectionTypeFullString);

        if (this.ReturnArity > 0)
        {
            sb.Append("<").Append(this.ReturnTypeArity).Append(">");
        }

        sb.Append(" ");
        sb.Append(this.Name).Append(" => global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.IProxyInvoker>(this).");
        sb.Append(CollectionType switch
        {
            CollectionTypeValues.List => "ProxyGetConfiguredNexusList<",
            _ => "INVALID"
        }).Append(this.ReturnTypeArity).Append(">(");
        
        sb.Append(this.Id).AppendLine(");");
    } 
    
    public void EmitNexusAccessor(StringBuilder sb)
    {
        sb.Append("    public ");
        sb.Append(CollectionTypeFullString);

        if (this.ReturnArity > 0)
        {
            sb.Append("<").Append(this.ReturnTypeArity).Append(">");
        }

        sb.Append(" ");
        sb.Append(this.Name).Append(" => global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.ICollectionStore>(this).");
        sb.Append(CollectionType switch
        {
            CollectionTypeValues.List => "GetList<",
            _ => "INVALID",
        }).Append(this.ReturnTypeArity).Append(">(");
        
        sb.Append(this.Id).Append(");");
    }
    
    public override string ToString()
    {
        var sb = SymbolUtilities.GetStringBuilder();

        // Use the short name description.
        if (this.ReturnArity > 0)
        {
            sb.Append(this.ReturnTypeSource).Append("(").Append(this.Id).Append(");");;
        }
        
        var stringMethod = sb.ToString();

        SymbolUtilities.ReturnStringBuilder(sb);

        return stringMethod;
    }
}
