using Xunit;

// I test toccano lo stato statico del motore -- Navigator.NavStatus, MissionSteps.Instance -- che
// e' condiviso da tutto il processo. Farli girare in parallelo li renderebbe dipendenti l'uno
// dall'altro. La concorrenza che si vuole provare sta DENTRO i test, non fra i test.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
