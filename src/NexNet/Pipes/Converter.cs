namespace NexNet.Pipes;

/// <summary>
/// The Converter class in the NexNet.Pipes namespace is a generic class that provides functionality to convert objects of one type to another.
/// The class takes two type parameters. The first type parameter represents the type of the object that will be converted, 
/// and the second type parameter represents the type the object will be converted to.
/// </summary>
/// <typeparam name="TInput">The type of the object that will be converted.</typeparam>
/// <typeparam name="TOutput">The type the object will be converted to.</typeparam>
public delegate TOutput Converter<TInput, out TOutput>(in TInput input);
