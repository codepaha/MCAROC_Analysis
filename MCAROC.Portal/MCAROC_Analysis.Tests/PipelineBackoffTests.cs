using MCAROC_Analysis.Services.Pipeline;

namespace MCAROC_Analysis.Tests;

/// <summary>#292 (plan §6.2): 2 min / 10 min / 30 min / 2 h schedule, ±20% jitter.</summary>
public sealed class PipelineBackoffTests
{
    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 10)]
    [InlineData(3, 30)]
    [InlineData(4, 120)]
    public void Each_attempt_stays_within_twenty_percent_of_its_schedule_step(int attempt, int baseMinutes)
    {
        var rng = new Random(1234);
        var baseDelay = TimeSpan.FromMinutes(baseMinutes);
        var low = baseDelay * 0.8;
        var high = baseDelay * 1.2;
        for (var i = 0; i < 500; i++)
        {
            var delay = PipelineBackoff.NextDelay(attempt, rng);
            Assert.True(delay >= low, $"trial {i}: {delay} below the 20% floor {low}");
            Assert.True(delay <= high, $"trial {i}: {delay} above the 20% ceiling {high}");
        }
    }

    [Fact]
    public void Jitter_actually_varies_the_delay_rather_than_always_returning_the_same_value()
    {
        var rng = new Random(99);
        var samples = Enumerable.Range(0, 20).Select(_ => PipelineBackoff.NextDelay(2, rng)).Distinct().Count();
        Assert.True(samples > 1, "20 samples at the same attempt number all came back identical — jitter isn't doing anything.");
    }

    [Fact]
    public void An_attempt_number_beyond_the_schedules_length_clamps_to_the_last_step_rather_than_throwing()
    {
        var rng = new Random(5);
        var delay = PipelineBackoff.NextDelay(999, rng);
        Assert.InRange(delay, TimeSpan.FromHours(2) * 0.8, TimeSpan.FromHours(2) * 1.2);
    }

    [Fact]
    public void Attempt_zero_or_negative_clamps_to_the_first_step_rather_than_throwing()
    {
        var rng = new Random(7);
        Assert.InRange(PipelineBackoff.NextDelay(0, rng), TimeSpan.FromMinutes(2) * 0.8, TimeSpan.FromMinutes(2) * 1.2);
        Assert.InRange(PipelineBackoff.NextDelay(-3, rng), TimeSpan.FromMinutes(2) * 0.8, TimeSpan.FromMinutes(2) * 1.2);
    }
}
