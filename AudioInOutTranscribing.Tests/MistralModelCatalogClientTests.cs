using AudioInOutTranscribing.App.Transcription;

namespace AudioInOutTranscribing.Tests;

public sealed class MistralModelCatalogClientTests
{
    [Fact]
    public void ParseModelIds_ReturnsDistinctSortedTranscriptionCapableIds()
    {
        var json = """
                   {
                     "object": "list",
                     "data": [
                       {
                         "id": "voxtral-mini-latest",
                         "capabilities": {
                           "audio_transcription": true
                         }
                       },
                       {
                         "id": "mistral-large-latest",
                         "capabilities": {
                           "audio_transcription": false
                         }
                       },
                       {
                         "id": "voxtral-mini-transcribe-2507",
                         "capabilities": {
                           "audio_transcription": true
                         }
                       },
                       {
                         "id": "voxtral-mini-transcribe-realtime-2602",
                         "capabilities": {
                           "audio_transcription": false,
                           "audio_transcription_realtime": true
                         }
                       },
                       {
                         "id": "VOXTRAL-MINI-LATEST",
                         "capabilities": {
                           "audio_transcription": true
                         }
                       }
                     ]
                   }
                   """;

        var ids = MistralModelCatalogClient.ParseModelIds(json);

        Assert.Equal(2, ids.Count);
        Assert.Equal("voxtral-mini-latest", ids[0]);
        Assert.Equal("voxtral-mini-transcribe-2507", ids[1]);
    }

    [Fact]
    public void ParseModelIds_ReturnsEmptyWhenShapeIsUnexpected()
    {
        var json = """{"foo":"bar"}""";

        var ids = MistralModelCatalogClient.ParseModelIds(json);

        Assert.Empty(ids);
    }

    [Fact]
    public void ParseModelIds_ReturnsEmptyWhenNoTranscriptionCapabilityPresent()
    {
        var json = """
                   {
                     "data": [
                       {
                         "id": "voxtral-mini-realtime-latest",
                         "capabilities": {
                           "audio_transcription": false,
                           "audio_transcription_realtime": true
                         }
                       },
                       {
                         "id": "mistral-medium-latest",
                         "capabilities": {
                           "audio_transcription": false
                         }
                       }
                     ]
                   }
                   """;

        var ids = MistralModelCatalogClient.ParseModelIds(json);

        Assert.Empty(ids);
    }

    [Fact]
    public void ParseModels_ExtractsDiarizationSupportFlags()
    {
        var json = """
                   {
                     "data": [
                       {
                         "id": "voxtral-mini-a",
                         "capabilities": {
                           "audio_transcription": true,
                           "diarization": true
                         }
                       },
                       {
                         "id": "voxtral-mini-b",
                         "capabilities": {
                           "audio_transcription": true,
                           "audio_transcription_diarization": false
                         }
                       },
                       {
                         "id": "voxtral-mini-c",
                         "capabilities": {
                           "audio_transcription": true
                         }
                       }
                     ]
                   }
                   """;

        var models = MistralModelCatalogClient.ParseModels(json);

        Assert.Equal(3, models.Count);
        Assert.Equal("voxtral-mini-a", models[0].Id);
        Assert.Equal(DiarizationSupport.Supported, models[0].DiarizationSupport);
        Assert.Equal("voxtral-mini-c", models[1].Id);
        Assert.Equal(DiarizationSupport.Unknown, models[1].DiarizationSupport);
        Assert.Equal("voxtral-mini-b", models[2].Id);
        Assert.Equal(DiarizationSupport.NotSupported, models[2].DiarizationSupport);
    }

    [Fact]
    public void ParseModels_PrefersSupportedDiarizationForDuplicateAlias()
    {
        var json = """
                   {
                     "data": [
                       {
                         "id": "voxtral-mini-latest",
                         "capabilities": {
                           "audio_transcription": true,
                           "diarization": false
                         }
                       },
                       {
                         "id": "VOXTRAL-MINI-LATEST",
                         "capabilities": {
                           "audio_transcription": true,
                           "diarization": true
                         }
                       }
                     ]
                   }
                   """;

        var models = MistralModelCatalogClient.ParseModels(json);

        var model = Assert.Single(models);
        Assert.Equal("voxtral-mini-latest", model.Id);
        Assert.Equal(DiarizationSupport.Supported, model.DiarizationSupport);
    }
}
