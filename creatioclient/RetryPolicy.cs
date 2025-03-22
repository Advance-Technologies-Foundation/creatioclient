namespace Creatio.Client {
	/// <summary>
	/// Define incrementation of delay between different retries: 
	/// <see cref="Simple"/> - no increment
	/// <see cref="Progressive"/> - multiply delay by the value of the current attempt of retry 
	/// </summary>
	public enum RetryPolicy {
		Simple,
		Progressive
	}
}
