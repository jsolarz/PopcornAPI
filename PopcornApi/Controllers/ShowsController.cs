﻿using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using PopcornApi.Attributes;
using PopcornApi.Database;
using PopcornApi.Models.Episode;
using PopcornApi.Models.Image;
using PopcornApi.Models.Rating;
using PopcornApi.Models.Show;
using PopcornApi.Models.Torrent.Show;
using PopcornApi.Services.Caching;
using PopcornApi.Services.Logging;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Text;
using System.Threading;
using PopcornApi.Extensions;

namespace PopcornApi.Controllers
{
    [Route("api/[controller]")]
    public class ShowsController : Controller
    {
        /// <summary>
        /// The logging service
        /// </summary>
        private readonly ILoggingService _loggingService;

        /// <summary>
        /// The caching service
        /// </summary>
        private readonly ICachingService _cachingService;

        /// <summary>
        /// Movies
        /// </summary>
        /// <param name="loggingService">The logging service</param>
        /// <param name="cachingService">The caching service</param>
        public ShowsController(ILoggingService loggingService, ICachingService cachingService)
        {
            _loggingService = loggingService;
            _cachingService = cachingService;
        }

        // GET api/shows
        [HttpGet]
        public async Task<IActionResult> Get([RequiredFromQuery] int page, [FromQuery] int limit,
            [FromQuery] int minimum_rating, [FromQuery] string query_term,
            [FromQuery] string genre, [FromQuery] string sort_by)
        {
            var nbShowsPerPage = 20;
            if (limit >= 20 && limit <= 50)
                nbShowsPerPage = limit;

            var currentPage = 1;
            if (page >= 1)
            {
                currentPage = page;
            }

            var queryTerm = string.Empty;
            if (!string.IsNullOrWhiteSpace(query_term))
            {
                queryTerm = query_term;
            }

            var genreFilter = string.Empty;
            if (!string.IsNullOrWhiteSpace(genre))
            {
                genreFilter = genre;
            }

            var hash = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(
                    $@"type=shows&page={page}&limit={limit}&minimum_rating={minimum_rating}&query_term={
                            query_term
                        }&genre={genre}&sort_by={sort_by}"));
            try
            {
                var cachedShows = await _cachingService.GetCache(hash);
                if (cachedShows != null)
                {
                    try
                    {
                        return Json(JsonConvert.DeserializeObject<ShowLightResponse>(cachedShows));
                    }
                    catch (Exception ex)
                    {
                        _loggingService.Telemetry.TrackException(ex);
                    }
                }
            }
            catch (Exception ex)
            {
                _loggingService.Telemetry.TrackException(ex);
            }

            using (var context = new PopcornContextFactory().CreateDbContext(new string[0]))
            {
                var skipParameter = new SqlParameter("@skip", (currentPage - 1) * nbShowsPerPage);
                var takeParameter = new SqlParameter("@take", nbShowsPerPage);
                var ratingParameter = new SqlParameter("@rating", minimum_rating);
                var queryParameter = new SqlParameter("@Keywords", queryTerm);
                var genreParameter = new SqlParameter("@genre", genreFilter);
                var query = @"
                    SELECT 
                        Show.Title, Show.Year, Rating.Percentage, Rating.Loved, Rating.Votes, Rating.Hated, Rating.Watching, Show.LastUpdated, Image.Banner, Image.Fanart, Image.Poster, Show.ImdbId, Show.TvdbId, Show.GenreNames, COUNT(*) OVER () as TotalCount
                    FROM 
                        ShowSet AS Show
                    INNER JOIN 
                        ImageShowSet AS Image
                    ON 
                        Image.Id = Show.ImagesId
                    INNER JOIN 
                        RatingSet AS Rating
                    ON 
                        Rating.Id = Show.RatingId
                    WHERE
                        1 = 1";

                if (minimum_rating > 0 && minimum_rating < 10)
                {
                    query += @" AND
                        Rating >= @rating";
                }

                if (!string.IsNullOrWhiteSpace(query_term))
                {
                    query += @" AND
                        FREETEXT(Title, @Keywords)";
                }

                if (!string.IsNullOrWhiteSpace(genre))
                {
                    query += @" AND
                        CONTAINS(GenreNames, @genre)";
                }

                if (!string.IsNullOrWhiteSpace(sort_by))
                {
                    switch (sort_by)
                    {
                        case "title":
                            query += " ORDER BY Show.Title ASC";
                            break;
                        case "year":
                            query += " ORDER BY Show.Year DESC";
                            break;
                        case "rating":
                            query += " ORDER BY Rating.Percentage DESC";
                            break;
                        case "loved":
                            query += " ORDER BY Rating.Loved DESC";
                            break;
                        case "votes":
                            query += " ORDER BY Rating.Votes DESC";
                            break;
                        case "watching":
                            query += " ORDER BY Rating.Watching DESC";
                            break;
                        case "date_added":
                            query += " ORDER BY Show.LastUpdated DESC";
                            break;
                        default:
                            query += " ORDER BY Show.LastUpdated DESC";
                            break;
                    }
                }
                else
                {
                    query += " ORDER BY Show.LastUpdated DESC";
                }

                query += @" OFFSET @skip ROWS 
                    FETCH NEXT @take ROWS ONLY";

                var showsQuery = await context.Database.ExecuteSqlQueryAsync(query, new CancellationToken(),
                    skipParameter, takeParameter,
                    ratingParameter, queryParameter,
                    genreParameter);
                var reader = showsQuery.DbDataReader;
                var count = 0;
                var shows = new List<ShowLightJson>();
                while (await reader.ReadAsync())
                {
                    var show = new ShowLightJson
                    {
                        Title = reader[0].GetType() != typeof(DBNull) ? (string) reader[0] : string.Empty,
                        Year = reader[1].GetType() != typeof(DBNull) ? (int) reader[1] : 0,
                        Rating = new RatingJson
                        {
                            Percentage = reader[2].GetType() != typeof(DBNull) ? (int) reader[2] : 0,
                            Loved = reader[3].GetType() != typeof(DBNull) ? (int) reader[3] : 0,
                            Votes = reader[4].GetType() != typeof(DBNull) ? (int) reader[4] : 0,
                            Hated = reader[5].GetType() != typeof(DBNull) ? (int) reader[5] : 0,
                            Watching = reader[6].GetType() != typeof(DBNull) ? (int) reader[6] : 0
                        },
                        Images = new ImageShowJson
                        {
                            Banner = reader[8].GetType() != typeof(DBNull) ? (string) reader[8] : string.Empty,
                            Fanart = reader[9].GetType() != typeof(DBNull) ? (string) reader[9] : string.Empty,
                            Poster = reader[10].GetType() != typeof(DBNull) ? (string) reader[10] : string.Empty,
                        },
                        ImdbId = reader[11].GetType() != typeof(DBNull) ? (string) reader[11] : string.Empty,
                        TvdbId = reader[12].GetType() != typeof(DBNull) ? (string) reader[12] : string.Empty,
                        Genres = reader[13].GetType() != typeof(DBNull) ? (string) reader[13] : string.Empty
                    };
                    shows.Add(show);
                    count = reader[14].GetType() != typeof(DBNull) ? (int) reader[14] : 0;
                }

                var response = new ShowLightResponse
                {
                    TotalShows = count,
                    Shows = shows
                };

                await _cachingService.SetCache(hash, JsonConvert.SerializeObject(response), TimeSpan.FromDays(1));
                return
                    Json(response);
            }
        }

        // GET api/shows/light/tt3640424
        [HttpGet("light/{imdb}")]
        public async Task<IActionResult> GetLight(string imdb)
        {
            var hash = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"light:{imdb}"));
            try
            {
                var cachedShow = await _cachingService.GetCache(hash);
                if (cachedShow != null)
                {
                    try
                    {
                        return Json(JsonConvert.DeserializeObject<ShowLightJson>(cachedShow));
                    }
                    catch (Exception ex)
                    {
                        _loggingService.Telemetry.TrackException(ex);
                    }
                }
            }
            catch (Exception ex)
            {
                _loggingService.Telemetry.TrackException(ex);
            }

            using (var context = new PopcornContextFactory().CreateDbContext(new string[0]))
            {
                var imdbParameter = new SqlParameter("@imdbId", imdb);
                var query = @"
                    SELECT 
                        Show.Title, Show.Year, Rating.Percentage, Rating.Loved, Rating.Votes, Rating.Hated, Rating.Watching, Show.LastUpdated, Image.Banner, Image.Fanart, Image.Poster, Show.ImdbId, Show.TvdbId, Show.GenreNames
                    FROM 
                        ShowSet AS Show
                    INNER JOIN 
                        ImageShowSet AS Image
                    ON 
                        Image.Id = Show.ImagesId
                    INNER JOIN 
                        RatingSet AS Rating
                    ON 
                        Rating.Id = Show.RatingId
                    WHERE
                        Show.ImdbId = @imdbId";
                var showQuery =
                    await context.Database.ExecuteSqlQueryAsync(query, new CancellationToken(), imdbParameter);
                var reader = showQuery.DbDataReader;
                var show = new ShowLightJson();
                while (await reader.ReadAsync())
                {
                    show.Title = reader[0].GetType() != typeof(DBNull) ? (string) reader[0] : string.Empty;
                    show.Year = reader[1].GetType() != typeof(DBNull) ? (int) reader[1] : 0;
                    show.Rating = new RatingJson
                    {
                        Percentage = reader[2].GetType() != typeof(DBNull) ? (int) reader[2] : 0,
                        Loved = reader[3].GetType() != typeof(DBNull) ? (int) reader[3] : 0,
                        Votes = reader[4].GetType() != typeof(DBNull) ? (int) reader[4] : 0,
                        Hated = reader[5].GetType() != typeof(DBNull) ? (int) reader[5] : 0,
                        Watching = reader[6].GetType() != typeof(DBNull) ? (int) reader[6] : 0
                    };
                    show.Images = new ImageShowJson
                    {
                        Banner = reader[8].GetType() != typeof(DBNull) ? (string) reader[8] : string.Empty,
                        Fanart = reader[9].GetType() != typeof(DBNull) ? (string) reader[9] : string.Empty,
                        Poster = reader[10].GetType() != typeof(DBNull) ? (string) reader[10] : string.Empty
                    };
                    show.ImdbId = reader[11].GetType() != typeof(DBNull) ? (string) reader[11] : string.Empty;
                    show.TvdbId = reader[12].GetType() != typeof(DBNull) ? (string) reader[12] : string.Empty;
                    show.Genres = reader[13].GetType() != typeof(DBNull) ? (string) reader[13] : string.Empty;
                }

                if (string.IsNullOrEmpty(show.ImdbId))
                    return BadRequest();

                await _cachingService.SetCache(hash, JsonConvert.SerializeObject(show));
                return Json(show);
            }
        }

        // GET api/shows/tt3640424
        [HttpGet("{imdb}")]
        public async Task<IActionResult> Get(string imdb)
        {
            var hash = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(imdb));
            try
            {
                var cachedShow = await _cachingService.GetCache(hash);
                if (cachedShow != null)
                {
                    try
                    {
                        return Json(JsonConvert.DeserializeObject<ShowJson>(cachedShow));
                    }
                    catch (Exception ex)
                    {
                        _loggingService.Telemetry.TrackException(ex);
                    }
                }
            }
            catch (Exception ex)
            {
                _loggingService.Telemetry.TrackException(ex);
            }
            using (var context = new PopcornContextFactory().CreateDbContext(new string[0]))
            {
                var show = await context.ShowSet.Include(a => a.Rating)
                    .Include(a => a.Episodes)
                    .ThenInclude(episode => episode.Torrents)
                    .ThenInclude(torrent => torrent.Torrent0)
                    .Include(a => a.Episodes)
                    .ThenInclude(episode => episode.Torrents)
                    .ThenInclude(torrent => torrent.Torrent1080p)
                    .Include(a => a.Episodes)
                    .ThenInclude(episode => episode.Torrents)
                    .ThenInclude(torrent => torrent.Torrent480p)
                    .Include(a => a.Episodes)
                    .ThenInclude(episode => episode.Torrents)
                    .ThenInclude(torrent => torrent.Torrent720p)
                    .Include(a => a.Genres)
                    .Include(a => a.Images)
                    .Include(a => a.Similars).AsQueryable()
                    .FirstOrDefaultAsync(a => a.ImdbId == imdb);
                if (show == null) return BadRequest();

                var showJson = ConvertShowToJson(show);
                await _cachingService.SetCache(hash, JsonConvert.SerializeObject(showJson));
                return Json(showJson);
            }
        }

        /// <summary>
        /// Convert a <see cref="Show"/> to a <see cref="ShowJson"/>
        /// </summary>
        /// <param name="show"></param>
        /// <returns></returns>
        private ShowJson ConvertShowToJson(Show show)
        {
            return new ShowJson
            {
                AirDay = show.AirDay,
                Rating = new RatingJson
                {
                    Hated = show.Rating?.Hated,
                    Loved = show.Rating?.Loved,
                    Percentage = show.Rating?.Percentage,
                    Votes = show.Rating?.Votes,
                    Watching = show.Rating?.Watching
                },
                Title = show.Title,
                Genres = show.Genres.Select(genre => genre.Name),
                Year = show.Year,
                ImdbId = show.ImdbId,
                Episodes = show.Episodes.Select(episode => new EpisodeShowJson
                {
                    DateBased = episode.DateBased,
                    EpisodeNumber = episode.EpisodeNumber,
                    Torrents = new TorrentShowNodeJson
                    {
                        Torrent_0 = new TorrentShowJson
                        {
                            Peers = episode.Torrents?.Torrent0?.Peers,
                            Seeds = episode.Torrents?.Torrent0?.Seeds,
                            Provider = episode.Torrents?.Torrent0?.Provider,
                            Url = episode.Torrents?.Torrent0?.Url
                        },
                        Torrent_1080p = new TorrentShowJson
                        {
                            Peers = episode.Torrents?.Torrent1080p?.Peers,
                            Seeds = episode.Torrents?.Torrent1080p?.Seeds,
                            Provider = episode.Torrents?.Torrent1080p?.Provider,
                            Url = episode.Torrents?.Torrent1080p?.Url
                        },
                        Torrent_720p = new TorrentShowJson
                        {
                            Peers = episode.Torrents?.Torrent720p?.Peers,
                            Seeds = episode.Torrents?.Torrent720p?.Seeds,
                            Provider = episode.Torrents?.Torrent720p?.Provider,
                            Url = episode.Torrents?.Torrent720p?.Url
                        },
                        Torrent_480p = new TorrentShowJson
                        {
                            Peers = episode.Torrents?.Torrent480p?.Peers,
                            Seeds = episode.Torrents?.Torrent480p?.Seeds,
                            Provider = episode.Torrents?.Torrent480p?.Provider,
                            Url = episode.Torrents?.Torrent480p?.Url
                        }
                    },
                    FirstAired = episode.FirstAired,
                    Title = episode.Title,
                    Overview = episode.Overview,
                    Season = episode.Season,
                    TvdbId = episode.TvdbId
                }).ToList(),
                TvdbId = show.TvdbId,
                AirTime = show.AirTime,
                Country = show.Country,
                Images = new ImageShowJson
                {
                    Banner = show.Images?.Banner,
                    Fanart = show.Images?.Fanart,
                    Poster = show.Images?.Poster
                },
                LastUpdated = show.LastUpdated,
                Network = show.Network,
                NumSeasons = show.NumSeasons,
                Runtime = show.Runtime,
                Slug = show.Slug,
                Status = show.Status,
                Synopsis = show.Synopsis,
                Similar = show.Similars.Select(a => a.TmdbId).ToList()
            };
        }
    }
}