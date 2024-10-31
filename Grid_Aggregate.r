# Load necessary libraries
library(sf)
library(dplyr)
library(future.apply)
library(mapview)

# Step 1: Set up parameters and generate random example data
set.seed(123)  # For reproducibility

# Adjust for demonstration; increase to 500,000 for full-scale
n_points <- 5000  
lon_min <- -74.1
lon_max <- -73.9
lat_min <- 40.7
lat_max <- 40.85
grid_size <- 500  # 500m grid cells

# Generate random coordinates for PU (Pickups)
PU <- data.frame(
  lon = runif(n_points, lon_min, lon_max),
  lat = runif(n_points, lat_min, lat_max)
)

# Generate random coordinates for DO (Dropoffs)
DO <- data.frame(
  lon = runif(n_points, lon_min, lon_max),
  lat = runif(n_points, lat_min, lat_max)
)

# Step 2: Convert PU and DO data frames to sf objects and transform to Web Mercator
PU_sf <- st_as_sf(PU, coords = c("lon", "lat"), crs = 4326) %>% 
  st_transform(crs = 3857)
DO_sf <- st_as_sf(DO, coords = c("lon", "lat"), crs = 4326) %>%
  st_transform(crs = 3857)

# Debug: Print transformed sample points
print("Sample transformed PU points:")
print(head(PU_sf))
print("Sample transformed DO points:")
print(head(DO_sf))

# Step 3: Define bounding box with buffer and create a 500m x 500m grid
bbox <- st_bbox(rbind(PU_sf, DO_sf)) + c(-500, -500, 500, 500)
print("Bounding Box with buffer:")
print(bbox)

grid <- st_make_grid(st_as_sfc(bbox), cellsize = grid_size, what = "polygons")
grid_sf <- st_sf(geometry = grid)

# Debug: Print sample grid cells to verify
print("Sample grid cells:")
print(head(grid_sf))

# Step 4: Define a function to count points within each grid cell and apply it in parallel
count_points_in_grid <- function(grid_cell, points_sf) {
  intersected_points <- st_intersects(points_sf, grid_cell, sparse = FALSE)
  count <- sum(intersected_points)  # Count points in this grid cell
  return(count)
}

# Set up parallel processing
plan(multisession, workers = parallel::detectCores() - 1)

# Count points for PU and DO in each grid cell
PU_counts <- future_sapply(grid_sf$geometry, count_points_in_grid, points_sf = PU_sf)
DO_counts <- future_sapply(grid_sf$geometry, count_points_in_grid, points_sf = DO_sf)

# Step 5: Combine counts into grid_sf and filter for non-zero counts
grid_sf$PU_count <- PU_counts
grid_sf$DO_count <- DO_counts
grid_sf <- grid_sf %>% filter(PU_count > 0 | DO_count > 0)

# Debug: Print non-zero cell counts for PU and DO
print("Grid cells with non-zero PU or DO counts:")
print(grid_sf %>% select(PU_count, DO_count))

# Step 6: Plot results using mapview
mapview(grid_sf, zcol = "PU_count") +
  mapview(grid_sf, zcol = "DO_count")
